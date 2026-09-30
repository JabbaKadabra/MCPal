using System.IO.Enumeration;

namespace MCPal.Bridge.Local;

/// <summary>Decides which tools of one local server the bridge exposes. Patterns are case-sensitive globs with <c>*</c> and <c>?</c>.</summary>
internal sealed class ToolFilter(IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
{
    public bool IsExposed(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        if (includes.Count > 0 && !includes.Any(pattern => Matches(pattern, toolName)))
        {
            return false;
        }

        return !excludes.Any(pattern => Matches(pattern, toolName));
    }

    private static bool Matches(string pattern, string toolName) =>
        FileSystemName.MatchesSimpleExpression(pattern, toolName, ignoreCase: false);
}
