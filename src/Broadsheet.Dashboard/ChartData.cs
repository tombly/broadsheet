using Broadsheet.Telemetry;

namespace Broadsheet.Dashboard;

public sealed record ChartPoint(DateTime X, double Value);

public sealed record ChartSeriesData(string Name, IReadOnlyList<ChartPoint> Points);

public sealed record CategoryPoint(string Category, double Value);

/// <summary>
/// Normalizes a <see cref="QueryResultTable"/> into shapes Telerik charts can bind to.
/// Chart queries are expected to be either [time, series, value] or [category, value].
/// </summary>
public static class ChartData
{
    /// <summary>For time/area charts: groups [time, series, value] rows into one series per distinct series value.</summary>
    public static IReadOnlyList<ChartSeriesData> TimeSeries(QueryResultTable table)
    {
        var hasSeriesColumn = table.Columns.Count >= 3;
        var seriesIndex = hasSeriesColumn ? 1 : -1;
        var valueIndex = hasSeriesColumn ? 2 : 1;

        var groups = new Dictionary<string, List<ChartPoint>>();
        foreach (var row in table.Rows)
        {
            var x = ToDateTime(row[0]);
            if (x is null)
            {
                continue;
            }

            var name = seriesIndex >= 0 ? row[seriesIndex]?.ToString() ?? string.Empty : "Value";
            if (!groups.TryGetValue(name, out var points))
            {
                points = [];
                groups[name] = points;
            }

            points.Add(new ChartPoint(x.Value, ToDouble(row[valueIndex])));
        }

        TrimTrailingBucket(groups);

        return groups.Select(group => new ChartSeriesData(group.Key, group.Value)).ToList();
    }

    // The query window ends at 'now' while bin() aligns to epoch boundaries, so the newest bucket
    // only holds a fraction of a grain of data and every time chart ends in a misleading dip.
    // Drop that bucket - unless it is the only one, where a partial point beats an empty chart.
    private static void TrimTrailingBucket(Dictionary<string, List<ChartPoint>> groups)
    {
        var buckets = groups.Values.SelectMany(points => points).Select(point => point.X).Distinct().ToList();
        if (buckets.Count < 2)
        {
            return;
        }

        var last = buckets.Max();
        foreach (var points in groups.Values)
        {
            points.RemoveAll(point => point.X == last);
        }
    }

    /// <summary>For bar/pie charts: maps [category, value] rows directly.</summary>
    public static IReadOnlyList<CategoryPoint> Categories(QueryResultTable table) =>
        table.Rows
            .Select(row => new CategoryPoint(row[0]?.ToString() ?? string.Empty, ToDouble(row[1])))
            .ToList();

    private static double ToDouble(object? value) => value switch
    {
        null => 0,
        double d => d,
        long l => l,
        int i => i,
        decimal m => (double)m,
        _ => double.TryParse(value.ToString(), out var parsed) ? parsed : 0
    };

    private static DateTime? ToDateTime(object? value) => value switch
    {
        DateTimeOffset dto => dto.LocalDateTime,
        DateTime dt => dt,
        null => null,
        _ => DateTime.TryParse(value.ToString(), out var parsed) ? parsed : null
    };
}
