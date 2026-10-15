namespace Broadsheet.Telemetry;

public enum VisualizationTypes
{
    Table,
    Stat,
    TimeChart,
    AreaChart,
    BarChart,
    PieChart
}

/// <summary>
/// The vertical size a workbook asks for. Azure names four (small, medium, large, full) but does not
/// publish the integers behind them, so an unrecognised value leaves the tile's own default standing.
/// </summary>
public enum TileHeight
{
    Small,
    Medium,
    Large,
    Full
}
