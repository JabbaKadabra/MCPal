using System.IO.Enumeration;

namespace MCPal.Contracts;

/// <summary>
/// Glob patterns over server and tool names, shared by the bridge's tool filter and the MCPal server's access grants.
/// <c>*</c> matches any run of characters, <c>?</c> exactly one; everything else is literal.
/// </summary>
public static class ToolPattern
{
    public const int MaxLength = 128;

    public static bool Matches(string pattern, string name, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(name);

        return FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase);
    }

    /// <summary>A pattern is valid when it is 1 to <see cref="MaxLength"/> characters of letters, digits, <c>_ - .</c> and the wildcards <c>*</c> and <c>?</c>.</summary>
    public static bool IsValid(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > MaxLength)
        {
            return false;
        }

        return pattern.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '*' or '?');
    }
}
