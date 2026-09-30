using System.Security.Cryptography;
using System.Text;

namespace MCPal.Cloud.Tunnel;

/// <summary>Builds the public tool name Claude sees: <c>server__tool</c>, sanitized and at most 64 characters.</summary>
internal static class ToolNaming
{
    public const int MaxLength = 64;

    private const string Separator = "__";
    private const int HashLength = 6;

    public static string Public(string serverName, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var name = Sanitize(serverName) + Separator + Sanitize(toolName);
        if (name.Length <= MaxLength)
        {
            return name;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serverName + "\0" + toolName)))[..HashLength];
        return string.Concat(name.AsSpan(0, MaxLength - HashLength - 1), "_", hash);
    }

    public const int MaxServerNameLength = 200;

    /// <summary>Whether the text can name a server: not blank, at most 200 characters and no control characters.</summary>
    public static bool IsValidServerName(string? serverName) =>
        !string.IsNullOrWhiteSpace(serverName) && serverName.Length <= MaxServerNameLength && !serverName.Any(char.IsControl);

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return builder.ToString();
    }
}
