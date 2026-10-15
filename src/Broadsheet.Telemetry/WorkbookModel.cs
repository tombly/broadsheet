using System.Globalization;

namespace Broadsheet.Telemetry;

/// <summary>How a workbook parameter gets its value.</summary>
public enum ParameterKinds
{
    /// <summary>A fixed value the workbook declares, or one the user types.</summary>
    Static,
    /// <summary>A KQL query returning one cell (text) or value/label rows (dropdown).</summary>
    Logs,
    /// <summary>An Azure Resource Graph query. Not executed; the declared default is used.</summary>
    ResourceGraph,
    /// <summary>The time window, supplied by the dashboard rather than the workbook.</summary>
    TimeRange
}

/// <summary>
/// One workbook parameter. Parameters resolve in declaration order because a later one's query can
/// substitute an earlier one's value, so evaluating them out of order leaves tokens unresolved.
/// </summary>
public sealed record WorkbookParameter(
    string Name,
    ParameterKinds Kind,
    bool IsDropdown,
    string? Label,
    string? DefaultValue,
    string? Query,
    IReadOnlyList<string> ResourceRefs);

/// <summary>
/// A workbook's own rule for when an item is shown, which is how it gates drill-downs on a
/// selection. Honouring it is what keeps an unattended board free of tiles nobody can populate.
/// </summary>
public sealed record ChartVisibility(string ParameterName, string Comparison, string? Value)
{
    public bool IsSatisfiedBy(ResolvedParameters parameters)
    {
        var actual = parameters.Value(ParameterName);
        var matches = string.Equals(actual, Value, StringComparison.Ordinal);

        return Comparison switch
        {
            "isEqualTo" => matches,
            "isNotEqualTo" => !matches,
            _ => true
        };
    }
}

/// <summary>One rendered tile: a query plus how the workbook says to draw it.</summary>
public sealed record BoardChart(
    string Name,
    string Title,
    VisualizationTypes Visualization,
    string Kql,
    IReadOnlyList<string> ResourceRefs,
    ChartVisibility? Visibility = null,
    double? WidthPercent = null,
    TileHeight? Height = null);

/// <summary>A tab, or the single synthetic section a flat workbook collapses into.</summary>
public sealed record BoardSection(string Key, string Label, IReadOnlyList<BoardChart> Charts);

/// <summary>
/// A workbook parsed into something renderable. <see cref="SourceId"/> is the fallback resource for
/// any query whose own <c>crossComponentResources</c> does not resolve.
/// </summary>
public sealed record WorkbookModel(
    string Title,
    string SourceId,
    IReadOnlyList<WorkbookParameter> Parameters,
    IReadOnlyList<BoardSection> Sections)
{
    public int ChartCount => Sections.Sum(section => section.Charts.Count);

    /// <summary>The window the workbook's author chose, which the board opens on. Null when it names none.</summary>
    public TimeSpan? DefaultTimeRange =>
        Parameters.FirstOrDefault(parameter => parameter.Kind == ParameterKinds.TimeRange)?.DefaultValue is { } value
        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;

    /// <summary>Charts in a section whose visibility rule the current selection satisfies.</summary>
    public static IEnumerable<BoardChart> Visible(BoardSection section, ResolvedParameters? parameters) =>
        section.Charts.Where(chart =>
            chart.Visibility is null || parameters is null || chart.Visibility.IsSatisfiedBy(parameters));
}

/// <summary>A workbook available to render, as listed from Azure.</summary>
public sealed record BoardDescriptor(string Name, string ResourceId);
