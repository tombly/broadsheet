# Broadsheet

A desktop wallboard for **Azure Monitor workbooks**. Point it at your subscriptions, pick a workbook, and it renders that workbook's charts as a live, self-refreshing board -- no per-board configuration and nothing about the workbook compiled in.

The workbook stays the source of truth. Its queries, parameters, tab structure, chart types and titles are read from Azure at load time, so editing a query in the portal changes what the board shows on the next refresh.

## What it does with a workbook

- **Lists** every workbook the signed-in identity can read across the configured subscriptions, as a picker.
- **Parses** it into sections and charts, handling both layouts found in the wild: tabbed (a links item plus groups) and flat (query items with text items as headings).
- **Resolves its parameters** in declaration order, because later ones reference earlier ones by token. Query-backed dropdowns become filters in the toolbar; a parameter with only one option does not earn a picker.
- **Runs each chart's KQL** against whatever resource that chart targets, falling back to the workbook's own source. A workbook spanning several App Insights resources works without special handling.
- **Renders** timechart, areachart, barchart, piechart and tiles as charts or stat tiles, and anything else as a grid.
- **Honours conditional visibility**, so a drill-down tile gated on a selection nobody will make is neither queried nor drawn.
- **Refreshes** every five minutes, filling in a section at a time. Automatic rotation cycles the time range so a long-lived display keeps moving.

Queries the Logs API cannot run are skipped rather than mis-rendered: Resource Graph and metrics items are dropped, and a Resource Graph *parameter* falls back to its declared default.

## Prerequisites

- **`az login`**. Auth is `DefaultAzureCredential`, and nothing is stored.
- The signed-in identity needs **Reader** or **Monitoring Reader** on the workbooks and on the resources their queries target.
- **.NET MAUI workload** (`dotnet workload install maui`) to build the desktop host. The `Broadsheet.Dashboard` library and the console harness build without it.
- **Windows is the platform this runs on daily.** Mac Catalyst builds from the same source and is the development target, but it trails: the Mac Catalyst SDK pins an exact Xcode version and refuses to build against an older one, so a Mac needs whatever Xcode the installed SDK demands.
- **A Telerik UI for Blazor license.** The UI is built on Telerik components, which are commercial. Bring your own license: set `TELERIK_LICENSE`, or drop your `telerik-license.txt` in `~/.telerik/`. Without one the UI runs but shows a watermark. See `nuget.config` for wiring up the Telerik feed.

## Configure

Create `~/.broadsheet/config.json`:

```json
{
  "subscriptions": [
    "00000000-0000-0000-0000-000000000000"
  ],
  "defaultBoard": "Overview"
}
```

Every workbook readable in those subscriptions that renders at least one chart shows up in the picker, and the board picker opens on `defaultBoard`. That name is matched loosely: an exact display name wins, otherwise the first board whose name contains the text, so there is no need to repeat a long title. Omit it, or name a board that no longer exists, and the first board opens.

The file lives outside the repository on purpose, so a checkout never carries subscription ids.

## Run

Start with the console harness. It needs neither the MAUI workload nor a Telerik license, so it is the fastest way to find out whether Broadsheet can read your workbooks at all:

```bash
az login
dotnet run --project samples/Broadsheet.Smoke                                        # every visible board
BROADSHEET_BOARDS="My Observability" dotnet run --project samples/Broadsheet.Smoke   # just these
```

For each board it prints the section and chart counts, the window the board declares, every parameter with its resolved value and option count, how many charts the workbook's own conditional visibility hides, and then runs the busiest section and reports what ran, what returned rows, and what failed. That output is also the right thing to paste into a bug report.

Then the desktop board:

```bash
dotnet build -t:Run -f net10.0-windows10.0.19041.0 src/Broadsheet.Ui/Broadsheet.Ui.csproj   # Windows
dotnet build -t:Run -f net10.0-maccatalyst src/Broadsheet.Ui/Broadsheet.Ui.csproj           # Mac Catalyst
```

## Layout

- `src/Broadsheet.Telemetry/` -- the data layer. Class library, no UI, nothing product-specific.
  - `WorkbookClient.cs` -- lists and loads workbooks over ARM.
  - `WorkbookParser.cs` -- turns `serializedData` into a `WorkbookModel`.
  - `WorkbookModel.cs` -- parameters, sections, charts, and the visibility rules attached to them.
  - `ResolvedParameters.cs` -- resolved values, labels and options, plus `{Token}` substitution.
  - `BroadsheetTelemetryService.cs` -- discovery, the parameter resolution chain, and query execution.
  - `QueryResultTable.cs` -- shape-agnostic result (columns + boxed rows; `AsRecords()` for binding).
- `src/Broadsheet.Dashboard/` -- the Blazor UI as a Razor Class Library (plain `dotnet build`, no MAUI workload).
  - `Components/Dashboard.razor` -- toolbar, board picker, stacked sections, auto-refresh.
  - `Components/QueryTile.razor` -- dispatches one result to a chart, stat or grid tile.
  - `ChartData.cs` -- normalizes any result into chart-bindable series or category points.
- `src/Broadsheet.Ui/` -- the MAUI Blazor Hybrid desktop host (Mac Catalyst / WinUI). Thin: `MauiProgram` + `MainPage` mount `BroadsheetApp`.
  - `wwwroot/dark-theme.css` -- dark swatch and the chart series palette, both derived from a handful of Telerik tokens.
- `samples/Broadsheet.Smoke/` -- the console harness.

## Binding, in one paragraph

`QueryResultTable` is deliberately untyped. `ChartData` normalizes it and `QueryTile` dispatches on the workbook's declared visualization: a table becomes a `TelerikGrid` with columns generated from `table.Columns`; a time chart or area chart expects rows of `[time, series, value]`, grouped into one series per distinct series value; bar and pie expect `[category, value]`. A query whose columns fit neither shape renders as a grid.

## Notes and gotchas

- **Token expiry:** when the `az` session lapses, calls throw `TelemetryAuthException` and do **not** re-prompt. The board surfaces it as a banner; re-run `az login`.
- **Credential ambiguity:** `DefaultAzureCredential` tries Visual Studio, environment variables and managed identity *before* the CLI. To force the `az` token, construct `BroadsheetTelemetryService` with an explicit `AzureCliCredential`.
- **Time window:** passed to the API through `QueryTimeRange`; the KQL itself carries no `ago()` filter. A workbook's `{TimeRange:grain}` bin size is computed in `GrainLiteral` to land at roughly 50-150 buckets.
- **One chart failing does not fail the board.** Each tile captures its own error; only an auth failure surfaces as a whole-board banner.
- **GUI-launched macOS apps** inherit a minimal `PATH` without Homebrew, so `MauiProgram` prepends the usual install directories to find `az`.

## License

MIT. See `LICENSE`. Telerik UI for Blazor is licensed separately and is not included.
