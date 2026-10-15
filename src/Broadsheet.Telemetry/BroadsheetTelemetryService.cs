using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace Broadsheet.Telemetry;

/// <summary>
/// Renders any Azure Monitor workbook: lists boards, parses one, resolves its parameters, and runs
/// its queries through the Logs API. Nothing here knows which product a board describes.
/// </summary>
public sealed class BroadsheetTelemetryService
{
    private readonly LogsQueryClient _client;
    private readonly WorkbookClient _workbooks;

    public BroadsheetTelemetryService(TokenCredential? credential = null)
    {
        var resolved = credential ?? new DefaultAzureCredential();
        _client = new LogsQueryClient(resolved);
        _workbooks = new WorkbookClient(resolved);
    }

    public Task<IReadOnlyList<BoardDescriptor>> Boards(IEnumerable<string> subscriptionIds, CancellationToken ct = default) =>
        _workbooks.List(subscriptionIds, ct);

    public Task<WorkbookModel> LoadBoard(string workbookResourceId, CancellationToken ct = default) =>
        _workbooks.Load(workbookResourceId, ct);

    /// <summary>
    /// Evaluates the board's parameters in declaration order, because a later one's query routinely
    /// substitutes the value an earlier one resolved to, including the resource it runs against.
    /// </summary>
    /// <param name="selections">User choices by parameter name; anything absent falls back to the workbook's default.</param>
    public async Task<ResolvedParameters> Resolve(
        WorkbookModel model,
        TimeSpan timeRange,
        IReadOnlyDictionary<string, string>? selections = null,
        CancellationToken ct = default)
    {
        var resolved = new ResolvedParameters();

        foreach (var parameter in model.Parameters)
        {
            switch (parameter.Kind)
            {
                case ParameterKinds.TimeRange:
                    resolved.Set(parameter.Name, GrainLiteral(timeRange), DescribeRange(timeRange));
                    break;

                case ParameterKinds.Static:
                case ParameterKinds.ResourceGraph:
                    // A Resource Graph parameter needs a different API; its declared default is
                    // usually a usable literal, such as a full resource id, so take that.
                    Apply(resolved, parameter, selections, parameter.DefaultValue ?? string.Empty);
                    break;

                case ParameterKinds.Logs:
                    await ResolveFromQuery(model, parameter, resolved, selections, ct);
                    break;
            }
        }

        return resolved;
    }

    public async Task<QueryResultTable> Run(
        WorkbookModel model,
        BoardChart chart,
        ResolvedParameters parameters,
        TimeSpan timeRange,
        CancellationToken ct = default)
    {
        var resource = ResolveResource(model, chart.ResourceRefs, parameters);
        var table = await Query(resource, parameters.Substitute(chart.Kql), timeRange, ct);
        return QueryResultTable.From(chart.Name, table);
    }

    /// <summary>
    /// Runs every chart in a section. One chart failing is captured in its own outcome rather than
    /// failing the refresh, since a board is usually still worth reading with one tile broken.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, QueryOutcome>> RefreshSection(
        WorkbookModel model,
        BoardSection section,
        ResolvedParameters parameters,
        TimeSpan timeRange,
        int maxConcurrency = 6,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(maxConcurrency);

        var outcomes = await Task.WhenAll(WorkbookModel.Visible(section, parameters).Select(async chart =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return new QueryOutcome { Id = chart.Name, Table = await Run(model, chart, parameters, timeRange, ct) };
            }
            catch (TelemetryAuthException)
            {
                throw; // auth is a whole-board problem, so let it surface as one banner
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new QueryOutcome { Id = chart.Name, Error = ex.Message };
            }
            finally
            {
                gate.Release();
            }
        }));

        // A workbook can repeat an item name across tabs; last one wins rather than throwing.
        var map = new Dictionary<string, QueryOutcome>(StringComparer.Ordinal);
        foreach (var outcome in outcomes)
            map[outcome.Id] = outcome;

        return map;
    }

    private async Task ResolveFromQuery(
        WorkbookModel model,
        WorkbookParameter parameter,
        ResolvedParameters resolved,
        IReadOnlyDictionary<string, string>? selections,
        CancellationToken ct)
    {
        try
        {
            var resource = ResolveResource(model, parameter.ResourceRefs, resolved);
            var table = await Query(resource, resolved.Substitute(parameter.Query!), TimeSpan.FromDays(1), ct);

            if (parameter.IsDropdown)
            {
                var options = ReadOptions(table);
                resolved.SetOptions(parameter.Name, options);
                Apply(resolved, parameter, selections, parameter.DefaultValue ?? options.FirstOrDefault()?.Value ?? string.Empty, options);
            }
            else
            {
                // A text parameter's query returns a single cell, which is the value.
                var cell = table.Rows.Count > 0 && table.Columns.Count > 0 ? table.Rows[0][0]?.ToString() : null;
                Apply(resolved, parameter, selections, cell ?? parameter.DefaultValue ?? string.Empty);
            }
        }
        catch (TelemetryAuthException)
        {
            throw;
        }
        catch (Exception)
        {
            // A parameter whose query fails must not take the board down: fall back to its default
            // and let the charts that depend on it report their own errors.
            Apply(resolved, parameter, selections, parameter.DefaultValue ?? string.Empty);
        }
    }

    private static void Apply(
        ResolvedParameters resolved,
        WorkbookParameter parameter,
        IReadOnlyDictionary<string, string>? selections,
        string fallback,
        IReadOnlyList<ParameterOption>? options = null)
    {
        var value = selections is not null && selections.TryGetValue(parameter.Name, out var chosen) ? chosen : fallback;
        var label = options?.FirstOrDefault(option => option.Value == value)?.Label;
        resolved.Set(parameter.Name, value, label);
    }

    private static IReadOnlyList<ParameterOption> ReadOptions(LogsTable table)
    {
        var valueIndex = IndexOf(table, "value");
        var labelIndex = IndexOf(table, "label");
        if (valueIndex < 0)
            return [];

        return table.Rows
            .Select(row => new ParameterOption(
                row[valueIndex]?.ToString() ?? string.Empty,
                (labelIndex >= 0 ? row[labelIndex]?.ToString() : null) ?? row[valueIndex]?.ToString() ?? string.Empty))
            .Where(option => option.Value.Length > 0)
            .ToList();
    }

    private static int IndexOf(LogsTable table, string column)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, column, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// A query targets whatever its <c>crossComponentResources</c> resolves to, falling back to the
    /// workbook's own source. Multi-resource references take the first; the rest are not queried.
    /// </summary>
    private static ResourceIdentifier ResolveResource(
        WorkbookModel model, IReadOnlyList<string> references, ResolvedParameters parameters)
    {
        foreach (var reference in references)
        {
            var candidate = parameters.Substitute(reference);
            if (candidate.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
                return new ResourceIdentifier(candidate.Split(',')[0].Trim());
        }

        if (string.IsNullOrWhiteSpace(model.SourceId))
            throw new InvalidOperationException($"No resource resolved for this query on '{model.Title}'.");

        return new ResourceIdentifier(model.SourceId);
    }

    private async Task<LogsTable> Query(ResourceIdentifier resource, string kql, TimeSpan timeRange, CancellationToken ct)
    {
        try
        {
            Response<LogsQueryResult> response = await _client.QueryResourceAsync(
                resource, kql, new QueryTimeRange(timeRange), cancellationToken: ct);
            return response.Value.Table;
        }
        catch (CredentialUnavailableException ex)
        {
            throw new TelemetryAuthException("No Azure credential available. Run `az login`.", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new TelemetryAuthException("Azure sign-in expired or failed. Run `az login` and retry.", ex);
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw new TelemetryAuthException(
                "Signed-in identity lacks read access to this resource (need Reader / Monitoring Reader).", ex);
        }
    }

    private static string DescribeRange(TimeSpan range) =>
        range.TotalDays >= 1 ? $"Last {range.TotalDays:0} days" : $"Last {range.TotalHours:0} hours";

    // Picks a bin size giving roughly 50-150 buckets across the window, matching the workbook's auto-grain.
    // Public so the dashboard can show the grain a given time range will query at.
    public static string GrainLiteral(TimeSpan range)
    {
        TimeSpan[] candidates =
        [
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
            TimeSpan.FromDays(1), TimeSpan.FromDays(7)
        ];

        var target = range / 100.0;
        var grain = candidates.FirstOrDefault(candidate => candidate >= target, candidates[^1]);

        if (grain.TotalDays >= 1 && grain.TotalDays % 1 == 0)
            return $"{(int)grain.TotalDays}d";

        if (grain.TotalHours >= 1 && grain.TotalHours % 1 == 0)
            return $"{(int)grain.TotalHours}h";

        return $"{(int)grain.TotalMinutes}m";
    }
}
