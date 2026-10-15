using System.Text.RegularExpressions;

namespace Broadsheet.Telemetry;

public sealed record ParameterOption(string Value, string Label);

/// <summary>
/// The parameter values a board is currently rendered with, and the option lists behind its dropdowns.
/// </summary>
/// <remarks>Tokens appear in queries, chart titles and section labels alike, so substitution is shared.</remarks>
public sealed partial class ResolvedParameters
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<ParameterOption>> _options = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Values => _values;

    public IReadOnlyDictionary<string, IReadOnlyList<ParameterOption>> Options => _options;

    public void Set(string name, string value, string? label = null)
    {
        _values[name] = value;
        _labels[name] = label ?? value;
    }

    public void SetOptions(string name, IReadOnlyList<ParameterOption> options) => _options[name] = options;

    public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Replaces every <c>{Name}</c> and <c>{Name:format}</c> token. An unresolved token is left as
    /// written, which keeps a broken query recognisable instead of silently changing its meaning.
    /// </summary>
    public string Substitute(string text) => TokenPattern().Replace(text, match =>
    {
        var name = match.Groups["name"].Value;
        var format = match.Groups["format"].Value;

        if (!_values.TryGetValue(name, out var value))
            return match.Value;

        return format switch
        {
            "label" => _labels.TryGetValue(name, out var label) ? label : value,
            _ => value
        };
    });

    [GeneratedRegex(@"\{(?<name>\w+)(?::(?<format>\w+))?\}")]
    private static partial Regex TokenPattern();
}
