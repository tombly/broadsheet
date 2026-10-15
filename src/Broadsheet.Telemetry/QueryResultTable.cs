using Azure.Monitor.Query.Models;

namespace Broadsheet.Telemetry;

/// <summary>
/// Shape-agnostic result of one KQL query: ordered column names plus boxed cell values.
/// Heterogeneous on purpose: every board has its own result shapes, and queries are edited in the portal.
/// </summary>
public sealed class QueryResultTable
{
    public required string QueryId { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    public static QueryResultTable From(string id, LogsTable table)
    {
        var columns = table.Columns.Select(column => column.Name).ToArray();
        var rows = new List<IReadOnlyList<object?>>(table.Rows.Count);

        foreach (var row in table.Rows)
        {
            var cells = new object?[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                cells[i] = row[i];
            }

            rows.Add(cells);
        }

        return new QueryResultTable { QueryId = id, Columns = columns, Rows = rows };
    }

    /// <summary>Rows as name-keyed dictionaries - convenient for Telerik grid binding and named chart series.</summary>
    public IEnumerable<IReadOnlyDictionary<string, object?>> AsRecords()
    {
        foreach (var row in Rows)
        {
            var record = new Dictionary<string, object?>(Columns.Count);
            for (var i = 0; i < Columns.Count; i++)
            {
                record[Columns[i]] = row[i];
            }

            yield return record;
        }
    }
}
