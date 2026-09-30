namespace MCPal.Cloud.Tunnel;

/// <summary>Compares agent versions as the portal needs it: is the agent older than the latest release?</summary>
internal static class AgentVersions
{
    /// <summary>
    /// True when <paramref name="agent"/> is a lower version than <paramref name="latest"/>. Only the numeric parts count (a
    /// missing part is 0, <c>+build</c> and <c>-prerelease</c> suffixes are ignored). Anything unparsable is never "older".
    /// </summary>
    public static bool IsOlder(string? agent, string? latest) =>
        TryParse(agent, out var current) && TryParse(latest, out var newest) && current < newest;

    private static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var numeric = value.Trim().Split('+', '-')[0];
        var parts = numeric.Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(part => !int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        var numbers = parts.Select(part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).Concat([0, 0, 0, 0]).Take(4).ToArray();
        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }
}
