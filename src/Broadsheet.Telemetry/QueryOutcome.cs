namespace Broadsheet.Telemetry;

/// <summary>Per-query result of a dashboard refresh: a table on success, an error message on failure.</summary>
public sealed class QueryOutcome
{
    public required string Id { get; init; }

    public QueryResultTable? Table { get; init; }

    public string? Error { get; init; }

    public bool Ok => Error is null;
}
