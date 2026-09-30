using System.Text.RegularExpressions;

namespace MCPal.Bridge.Config;

/// <summary>
/// Expands <c>${NAME}</c> and <c>${NAME:-default}</c> in config values, the syntax Claude Code uses in <c>.mcp.json</c>.
/// <c>$$</c> is a literal <c>$</c>. A variable that is not set and has no default is an error, never an empty string.
/// </summary>
internal static partial class EnvironmentExpander
{
    public static string Expand(string value, IReadOnlyDictionary<string, string?> environment, string context)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        return Token().Replace(value, match =>
        {
            if (match.Value == "$$")
            {
                return "$";
            }

            var name = match.Groups["name"].Value;
            environment.TryGetValue(name, out var actual);
            if (match.Groups["default"].Success)
            {
                return string.IsNullOrEmpty(actual) ? match.Groups["default"].Value : actual;
            }

            return actual ?? throw new BridgeConfigException($"{context}: environment variable '{name}' is not set.");
        });
    }

    [GeneratedRegex(@"\$\$|\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::-(?<default>[^}]*))?\}")]
    private static partial Regex Token();
}
