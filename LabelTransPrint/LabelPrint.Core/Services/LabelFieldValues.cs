using System.Text.RegularExpressions;
using System.Globalization;

namespace LabelTransPrint.Services;

/// <summary>Values for application-defined template fields, keyed by their complete token.</summary>
public sealed class LabelFieldValues
{
    private static readonly Regex TokenPattern = new(@"\{\{[^{}]+\}\}|\{[^{}]+\}", RegexOptions.Compiled);
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string token, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!TokenPattern.IsMatch(token) || TokenPattern.Match(token).Value != token)
            throw new ArgumentException("字段名称必须是完整的占位符，例如 {{PartNumber}}。", nameof(token));
        _values[token] = value ?? string.Empty;
    }

    public string Resolve(string? template)
        => TokenPattern.Replace(template ?? string.Empty, match =>
        {
            if (_values.TryGetValue(match.Value, out var value))
                return value;
            if (match.Value.StartsWith("{{Now:", StringComparison.Ordinal))
                return DateTime.Now.ToString(match.Value[6..^2], CultureInfo.InvariantCulture);
            return match.Value;
        });

    public bool TryGetValue(string token, out string value)
        => _values.TryGetValue(token, out value!);

    public IReadOnlyList<string> FindMissing(string? template)
        => TokenPattern.Matches(template ?? string.Empty).Select(match => match.Value)
            .Where(token => !_values.ContainsKey(token) && !token.StartsWith("{{Now:", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
