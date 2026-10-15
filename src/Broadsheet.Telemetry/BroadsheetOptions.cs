using System.Text.Json;

namespace Broadsheet.Telemetry;

/// <summary>Host configuration: which subscriptions to offer boards from, and which board opens.</summary>
/// <remarks>Any workbook the signed-in identity can read in these subscriptions appears in the picker.</remarks>
public sealed record BroadsheetOptions(IReadOnlyList<string> Subscriptions, string? DefaultBoard = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The config file, deliberately outside the repository so a checkout carries no subscription ids.</summary>
    public static string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".broadsheet", "config.json");

    /// <summary>What to tell a user who has not written the config yet. Both hosts show this text.</summary>
    public static string SetupHint =>
        $"No subscriptions configured. Create {ConfigPath} containing {{ \"subscriptions\": [\"<subscription-id>\"] }}";

    /// <summary>Reads <see cref="ConfigPath"/>. An absent file yields no subscriptions; a malformed one throws.</summary>
    public static BroadsheetOptions Load()
    {
        if (!File.Exists(ConfigPath))
            return new BroadsheetOptions([]);

        using var stream = File.OpenRead(ConfigPath);
        var file = JsonSerializer.Deserialize<ConfigFile>(stream, SerializerOptions);

        return new BroadsheetOptions(file?.Subscriptions ?? [], file?.DefaultBoard);
    }

    /// <summary>
    /// The board to open with. An exact name wins, then the first name containing the configured
    /// text, so a config can say "Overview" rather than repeating a long display name. A name that
    /// matches nothing opens the first board rather than nothing, because a wall display left blank
    /// reads as broken.
    /// </summary>
    public BoardDescriptor? PickDefault(IReadOnlyList<BoardDescriptor> boards)
    {
        if (string.IsNullOrWhiteSpace(DefaultBoard))
            return boards.FirstOrDefault();

        return boards.FirstOrDefault(board => string.Equals(board.Name, DefaultBoard, StringComparison.OrdinalIgnoreCase))
            ?? boards.FirstOrDefault(board => board.Name.Contains(DefaultBoard, StringComparison.OrdinalIgnoreCase))
            ?? boards.FirstOrDefault();
    }

    private sealed record ConfigFile(IReadOnlyList<string>? Subscriptions, string? DefaultBoard);
}
