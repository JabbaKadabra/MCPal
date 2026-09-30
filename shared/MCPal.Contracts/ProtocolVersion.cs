namespace MCPal.Contracts;

/// <summary>
/// Version of the bridge tunnel protocol. The MCPal server rejects bridges with an unknown major version.
/// Minor versions are additive: 1.1 adds <c>CancelCall</c>, structured content and output schemas, and trace propagation.
/// </summary>
public static class ProtocolVersion
{
    public const string Current = "1.1";

    public const int Major = 1;

    /// <summary>The first minor version whose bridges understand <see cref="IBridgeHubClient.CancelCall"/>.</summary>
    public const int CancelCallMinor = 1;

    /// <summary>True when <paramref name="version"/> ("major.minor") is at least <paramref name="major"/>.<paramref name="minor"/>. Unparsable versions are never at least anything.</summary>
    public static bool AtLeast(string? version, int major, int minor)
    {
        var parts = version?.Split('.');
        if (parts is not { Length: >= 2 } || !int.TryParse(parts[0], out var actualMajor) || !int.TryParse(parts[1], out var actualMinor))
        {
            return false;
        }

        return actualMajor > major || (actualMajor == major && actualMinor >= minor);
    }
}
