using System.Diagnostics;
using Broadsheet.Telemetry;

// Proves the generic path end to end: discover boards, parse one, resolve its parameter chain, and
// run a section's queries against whatever resource that board targets. Requires `az login`.

var config = BroadsheetOptions.Load();
if (config.Subscriptions.Count == 0)
{
    Console.Error.WriteLine(BroadsheetOptions.SetupHint);
    return 1;
}

// Every board the signed-in identity can see, or the ones named in BROADSHEET_BOARDS.
var wanted = (Environment.GetEnvironmentVariable("BROADSHEET_BOARDS") ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var service = new BroadsheetTelemetryService();

try
{
    var boards = await service.Boards(config.Subscriptions);
    var names = wanted.Length > 0 ? wanted : boards.Select(board => board.Name).ToArray();

    foreach (var name in names)
    {
        var descriptor = boards.FirstOrDefault(board => board.Name == name);
        if (descriptor is null)
        {
            Console.WriteLine($"== {name}: not visible\n");
            continue;
        }

        var model = await service.LoadBoard(descriptor.ResourceId);

        // The window the board's own author chose, so these numbers match what the UI shows.
        var timeRange = model.DefaultTimeRange ?? TimeSpan.FromDays(1);
        var window = timeRange.TotalDays >= 1 ? $"{timeRange.TotalDays:0}d" : $"{timeRange.TotalHours:0}h";

        Console.WriteLine($"== {model.Title}  ({model.Sections.Count} sections, {model.ChartCount} charts, "
            + $"{window} window{(model.DefaultTimeRange is null ? " - board declares none" : "")})");

        var sw = Stopwatch.StartNew();
        var parameters = await service.Resolve(model, timeRange);
        sw.Stop();

        Console.WriteLine($"   parameters resolved in {sw.Elapsed.TotalSeconds:0.0}s:");
        foreach (var parameter in model.Parameters)
        {
            var value = parameters.Value(parameter.Name) ?? "";
            var options = parameters.Options.TryGetValue(parameter.Name, out var list) ? $"  [{list.Count} options]" : "";
            var shown = value.Length > 54 ? "..." + value[^51..] : value;
            Console.WriteLine($"     {parameter.Name,-22} {parameter.Kind,-14} = {shown}{options}");
        }

        var gated = model.Sections.Sum(s => s.Charts.Count - WorkbookModel.Visible(s, parameters).Count());
        Console.WriteLine($"   {gated} chart(s) hidden by the workbook's own conditional visibility");

        // A workbook can hold nothing this renders (all Resource Graph, or text only).
        var section = model.Sections.MaxBy(s => s.Charts.Count);
        if (section is null)
        {
            Console.WriteLine("   nothing to run\n");
            continue;
        }

        sw.Restart();
        var outcomes = await service.RefreshSection(model, section, parameters, timeRange);
        sw.Stop();

        var ok = outcomes.Values.Count(o => o.Ok);
        var withRows = outcomes.Values.Count(o => o.Ok && o.Table is { Rows.Count: > 0 });
        Console.WriteLine($"   section '{parameters.Substitute(section.Label)}': {ok}/{outcomes.Count} ran, "
            + $"{withRows} returned rows, in {sw.Elapsed.TotalSeconds:0.0}s");

        foreach (var failure in outcomes.Values.Where(o => !o.Ok).Take(3))
            Console.WriteLine($"     FAIL {failure.Id}: {failure.Error?[..Math.Min(110, failure.Error.Length)]}");

        Console.WriteLine();
    }
}
catch (TelemetryAuthException ex)
{
    Console.Error.WriteLine($"Auth problem: {ex.Message}");
    return 1;
}

return 0;
