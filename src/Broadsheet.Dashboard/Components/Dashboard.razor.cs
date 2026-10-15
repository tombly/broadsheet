using Broadsheet.Telemetry;
using Microsoft.AspNetCore.Components;

namespace Broadsheet.Dashboard.Components;

internal sealed record TimeRangePreset(string Key, string Label, TimeSpan Span);

internal enum RotationMode
{
    Manual,
    Automatic
}

public partial class Dashboard : IDisposable
{
    [Inject] public BroadsheetTelemetryService Telemetry { get; set; } = default!;

    [Inject] public BroadsheetOptions Options { get; set; } = default!;

    private static readonly IReadOnlyList<TimeRangePreset> Ranges =
    [
        new("1h", "Last hour", TimeSpan.FromHours(1)),
        new("6h", "Last 6 hours", TimeSpan.FromHours(6)),
        new("24h", "Last 24 hours", TimeSpan.FromHours(24)),
        new("3d", "Last 3 days", TimeSpan.FromDays(3)),
        new("7d", "Last 7 days", TimeSpan.FromDays(7)),
        new("30d", "Last 30 days", TimeSpan.FromDays(30))
    ];

    private const string DefaultRangeKey = "30d";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyList<RotationMode> Rotations = Enum.GetValues<RotationMode>();

    // Ranges the Automatic rotation cycles through on each auto-refresh tick.
    private static readonly string[] RotationRanges = ["24h", "7d", "30d"];

    private readonly CancellationTokenSource _cts = new();

    private IReadOnlyList<BoardDescriptor> _boards = [];
    private string? _boardId;
    private WorkbookModel? _model;
    private ResolvedParameters? _parameters;

    /// <summary>User overrides by parameter name; anything absent falls back to the board's own default.</summary>
    private readonly Dictionary<string, string> _selections = new(StringComparer.OrdinalIgnoreCase);

    private string _rangeKey = DefaultRangeKey;
    private RotationMode _rotation = RotationMode.Manual;

    private readonly Dictionary<string, QueryOutcome> _results = new(StringComparer.Ordinal);
    private int _completed;
    private bool _isRefreshing;
    private string? _authError;
    private DateTimeOffset? _lastRefresh;

    private TimeSpan Range => Ranges.First(range => range.Key == _rangeKey).Span;

    private string GrainLabel => BroadsheetTelemetryService.GrainLiteral(Range);

    private int ChartCount => _model?.Sections.Sum(section => VisibleCharts(section).Count) ?? 0;

    /// <summary>
    /// The charts a board actually draws right now. A workbook gates its drill-down tiles on a
    /// selection, and an unattended board never makes one, so those tiles stay out entirely.
    /// </summary>
    private List<BoardChart> VisibleCharts(BoardSection section) =>
        WorkbookModel.Visible(section, _parameters).ToList();

    /// <summary>Only parameters with a real choice earn a picker; derived and single-option ones do not.</summary>
    private IEnumerable<WorkbookParameter> Pickers =>
        _model?.Parameters.Where(parameter =>
            parameter.IsDropdown
            && _parameters is not null
            && _parameters.Options.TryGetValue(parameter.Name, out var options)
            && options.Count > 1) ?? [];

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _boards = await Telemetry.Boards(Options.Subscriptions, _cts.Token);
            var opening = Options.PickDefault(_boards);
            if (opening is not null)
                await SelectBoard(opening.ResourceId);
        }
        catch (TelemetryAuthException ex)
        {
            _authError = ex.Message;
            return;
        }

        _ = AutoRefreshLoop(_cts.Token);
    }

    private async Task SelectBoard(string boardId)
    {
        _boardId = boardId;
        _selections.Clear();
        _results.Clear();
        _model = null;

        try
        {
            _model = await Telemetry.LoadBoard(boardId, _cts.Token);
            _rangeKey = NearestRange(_model.DefaultTimeRange);
        }
        catch (TelemetryAuthException ex)
        {
            _authError = ex.Message;
            return;
        }

        await Refresh();
    }

    private async Task OnParameterChanged(string name, string value)
    {
        _selections[name] = value;
        await Refresh();
    }

    private async Task AutoRefreshLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_rotation == RotationMode.Automatic)
                    AdvanceRotation();

                await Refresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Steps to the next range in the cycle; a range outside the cycle (index -1) restarts at the first.
    private void AdvanceRotation()
    {
        var index = Array.IndexOf(RotationRanges, _rangeKey);
        _rangeKey = RotationRanges[(index + 1) % RotationRanges.Length];
    }

    /// <summary>
    /// The preset closest to the window a board asks for, since a workbook states an arbitrary
    /// duration and the picker offers a fixed set. A board naming none opens on the app default.
    /// </summary>
    private static string NearestRange(TimeSpan? preferred) =>
        preferred is { } window
            ? Ranges.MinBy(range => Math.Abs((range.Span - window).Ticks))!.Key
            : DefaultRangeKey;

    private Task OnFilterChanged() => Refresh();

    private async Task Refresh()
    {
        if (_isRefreshing || _model is null)
            return;

        _isRefreshing = true;
        _completed = 0;
        _authError = null;
        await InvokeAsync(StateHasChanged);

        try
        {
            // Parameters are re-resolved per refresh: their option lists are themselves queries, so
            // a newly reported value appears without restarting the app.
            _parameters = await Telemetry.Resolve(_model, Range, _selections, _cts.Token);

            // Section at a time so a long board fills in as it goes rather than staying blank.
            foreach (var section in _model.Sections)
            {
                var outcomes = await Telemetry.RefreshSection(_model, section, _parameters, Range, ct: _cts.Token);
                foreach (var (id, outcome) in outcomes)
                    _results[id] = outcome;

                _completed += VisibleCharts(section).Count;
                await InvokeAsync(StateHasChanged);
            }

            _lastRefresh = DateTimeOffset.Now;
        }
        catch (TelemetryAuthException ex)
        {
            _authError = ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _isRefreshing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private string Text(string? value) => _parameters is null ? value ?? string.Empty : _parameters.Substitute(value ?? string.Empty);

    private QueryOutcome? Outcome(BoardChart chart) =>
        _results.TryGetValue(chart.Name, out var outcome) ? outcome : null;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
