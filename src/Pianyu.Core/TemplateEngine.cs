using System.Text.RegularExpressions;

namespace Pianyu.Core;

public sealed record TemplateVariable(string Name, string DefaultValue);

public static partial class TemplateEngine
{
    [GeneratedRegex(@"\{(?<name>[a-zA-Z_][a-zA-Z0-9_-]*)(?:=(?<default>[^{}]*))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariableRegex();

    public static IReadOnlyList<TemplateVariable> Parse(string text) =>
        VariableRegex().Matches(text)
            .Select(match => new TemplateVariable(
                match.Groups["name"].Value,
                match.Groups["default"].Success ? match.Groups["default"].Value : string.Empty))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    public static string Render(string text, IReadOnlyDictionary<string, string> values) =>
        VariableRegex().Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            if (values.TryGetValue(name, out var value))
            {
                return value;
            }

            return match.Groups["default"].Success ? match.Groups["default"].Value : string.Empty;
        });
}
