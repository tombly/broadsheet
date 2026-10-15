using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broadsheet.Telemetry;

/// <summary>
/// Turns a workbook's <c>serializedData</c> into a <see cref="WorkbookModel"/>.
/// </summary>
/// <remarks>
/// Two layouts exist in the wild: tabbed (a links item plus groups gated on the tab parameter) and
/// flat (query items at the top level, text items acting as headings). Both parse to sections.
/// </remarks>
public static class WorkbookParser
{
    private const int TextItem = 1;
    private const int QueryItem = 3;
    private const int ParametersItem = 9;
    private const int LinksItem = 11;
    private const int GroupItem = 12;

    private const int TextParameter = 1;
    private const int DropdownParameter = 2;
    private const int TimeRangeParameter = 4;

    private const int LogsQuery = 0;

    public static WorkbookModel Parse(string title, string sourceId, string serializedData)
    {
        var root = JsonNode.Parse(serializedData)
            ?? throw new InvalidOperationException("Workbook serializedData is not JSON.");
        var items = root["items"]?.AsArray() ?? [];

        var parameters = ParseParameters(items);
        var tabLabels = ParseTabLabels(items);
        var sections = ParseSections(items, tabLabels);

        return new WorkbookModel(title, sourceId, parameters, sections);
    }

    private static List<WorkbookParameter> ParseParameters(JsonArray items)
    {
        var declarations = items
            .FirstOrDefault(item => Type(item) == ParametersItem)?["content"]?["parameters"]?.AsArray();
        if (declarations is null)
            return [];

        var parameters = new List<WorkbookParameter>();
        foreach (var declaration in declarations)
        {
            if (declaration is null)
                continue;

            var name = Text(declaration, "name");
            if (string.IsNullOrEmpty(name))
                continue;

            var type = Int(declaration, "type") ?? TextParameter;
            var query = Text(declaration, "query");
            var queryType = Int(declaration, "queryType") ?? LogsQuery;

            var kind = type switch
            {
                TimeRangeParameter => ParameterKinds.TimeRange,
                _ when string.IsNullOrWhiteSpace(query) => ParameterKinds.Static,
                _ when queryType != LogsQuery => ParameterKinds.ResourceGraph,
                _ => ParameterKinds.Logs
            };

            parameters.Add(new WorkbookParameter(
                name,
                kind,
                type == DropdownParameter,
                Text(declaration, "label"),
                ScalarValue(declaration["value"]),
                query,
                Strings(declaration["crossComponentResources"])));
        }

        return parameters;
    }

    /// <summary>Tab value (the group's conditional-visibility match) to its human label.</summary>
    private static Dictionary<string, string> ParseTabLabels(JsonArray items)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(item => Type(item) == LinksItem))
        {
            foreach (var link in item?["content"]?["links"]?.AsArray() ?? [])
            {
                var value = Text(link, "subTarget");
                var label = Text(link, "linkLabel");
                if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(label))
                    labels[value] = label;
            }
        }

        return labels;
    }

    private static List<BoardSection> ParseSections(JsonArray items, Dictionary<string, string> tabLabels)
    {
        var sections = new List<BoardSection>();
        var loose = new List<BoardChart>();
        var looseHeading = string.Empty;

        foreach (var item in items)
        {
            switch (Type(item))
            {
                case GroupItem:
                    var key = Text(item?["conditionalVisibility"], "value") ?? Text(item, "name") ?? $"section-{sections.Count}";
                    var charts = CollectCharts(item);
                    if (charts.Count > 0)
                        sections.Add(new BoardSection(key, Label(key, tabLabels, item), charts));
                    break;

                case QueryItem:
                    // Flat layout: the nearest preceding text item is the heading for the run of
                    // charts that follows it, mirroring how the workbook reads on screen.
                    var chart = ToChart(item);
                    if (chart is not null)
                        loose.Add(chart);
                    break;

                case TextItem when loose.Count > 0:
                    sections.Add(new BoardSection(looseHeading, looseHeading, loose.ToList()));
                    loose.Clear();
                    looseHeading = Heading(item) ?? string.Empty;
                    break;

                case TextItem:
                    looseHeading = Heading(item) ?? looseHeading;
                    break;
            }
        }

        if (loose.Count > 0)
            sections.Add(new BoardSection(looseHeading, looseHeading, loose));

        return sections;
    }

    private static string Label(string key, Dictionary<string, string> tabLabels, JsonNode? group) =>
        tabLabels.TryGetValue(key, out var label) ? label : Text(group, "name") ?? key;

    /// <summary>Charts inside a group, including nested groups.</summary>
    private static List<BoardChart> CollectCharts(JsonNode? group)
    {
        var charts = new List<BoardChart>();
        foreach (var item in group?["content"]?["items"]?.AsArray() ?? [])
        {
            if (Type(item) == GroupItem)
            {
                charts.AddRange(CollectCharts(item));
                continue;
            }

            var chart = ToChart(item);
            if (chart is not null)
                charts.Add(chart);
        }

        return charts;
    }

    private static BoardChart? ToChart(JsonNode? item)
    {
        var content = item?["content"];
        var kql = Text(content, "query");
        if (string.IsNullOrWhiteSpace(kql))
            return null;

        // Only Logs queries run through the KQL client; a workbook may also carry Resource Graph
        // or metrics items, which need a different API and are dropped rather than mis-rendered.
        if ((Int(content, "queryType") ?? LogsQuery) != LogsQuery)
            return null;

        var name = Text(item, "name") ?? Text(content, "title") ?? kql[..Math.Min(24, kql.Length)];
        return new BoardChart(
            name,
            Text(content, "title") ?? name,
            ToVisualization(Text(content, "visualization")),
            kql,
            Strings(content?["crossComponentResources"]),
            ToVisibility(item?["conditionalVisibility"]),
            ToWidth(item),
            ToHeight(content));
    }

    /// <summary>The vertical size the workbook asks for; an unmapped value leaves the tile's default.</summary>
    private static TileHeight? ToHeight(JsonNode? content) => Int(content, "size") switch
    {
        0 => TileHeight.Small,
        1 => TileHeight.Medium,
        2 => TileHeight.Large,
        3 => TileHeight.Full,
        _ => null
    };

    /// <summary>The percentage of its row the workbook gives this item; absent means the whole row.</summary>
    private static double? ToWidth(JsonNode? item)
    {
        // A workbook writes thirds as "33.33", and the separator is always a period regardless of
        // the machine's locale.
        if (!double.TryParse(
                Text(item, "customWidth")?.TrimEnd('%'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return null;

        return percent;
    }

    private static ChartVisibility? ToVisibility(JsonNode? node)
    {
        var parameterName = Text(node, "parameterName");
        var comparison = Text(node, "comparison");

        return string.IsNullOrEmpty(parameterName) || string.IsNullOrEmpty(comparison)
            ? null
            : new ChartVisibility(parameterName, comparison, Text(node, "value"));
    }

    private static VisualizationTypes ToVisualization(string? visualization) => visualization switch
    {
        "timechart" => VisualizationTypes.TimeChart,
        "barchart" => VisualizationTypes.BarChart,
        "areachart" => VisualizationTypes.AreaChart,
        "piechart" => VisualizationTypes.PieChart,
        "tiles" => VisualizationTypes.Stat,
        _ => VisualizationTypes.Table
    };

    /// <summary>First markdown heading in a text item, used as the section name in a flat workbook.</summary>
    private static string? Heading(JsonNode? item)
    {
        var markdown = Text(item?["content"], "json");
        if (string.IsNullOrWhiteSpace(markdown))
            return null;

        var line = markdown
            .Split('\n')
            .FirstOrDefault(candidate => candidate.TrimStart().StartsWith('#'));

        return line?.TrimStart().TrimStart('#').Trim();
    }

    private static int? Type(JsonNode? item) => Int(item, "type");

    private static int? Int(JsonNode? node, string property) =>
        node?[property] is { } value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<int>() : null;

    private static string? Text(JsonNode? node, string property) =>
        node?[property] is { } value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>
    /// A parameter default is usually a string. A time range states itself as an object holding
    /// durationMs, which comes back as that number so the board can read the window the author chose.
    /// </summary>
    private static string? ScalarValue(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Object => node["durationMs"]?.GetValueKind() == JsonValueKind.Number
            ? node["durationMs"]!.GetValue<double>().ToString(CultureInfo.InvariantCulture)
            : null,
        _ => null
    };

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node?.AsArray()
            .Select(element => element?.GetValue<string>())
            .Where(element => !string.IsNullOrEmpty(element))
            .Select(element => element!)
            .ToList() ?? [];
}
