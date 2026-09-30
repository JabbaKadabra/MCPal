namespace MCPal.Cloud.OAuth;

/// <summary>Which redirect URIs clients may register and use: the claude.ai callback and loopback URIs on any port.</summary>
internal static class RedirectUriPolicy
{
    public const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";

    public static bool IsAllowedForRegistration(string redirectUri)
    {
        if (!TryParse(redirectUri, out var uri))
        {
            return false;
        }

        return redirectUri == ClaudeCallback || IsLoopback(uri);
    }

    /// <summary>Exact match, except loopback URIs where the port may differ (RFC 8252).</summary>
    public static bool Matches(IEnumerable<string> registered, string requested)
    {
        if (!TryParse(requested, out var requestedUri))
        {
            return false;
        }

        foreach (var candidate in registered)
        {
            if (candidate == requested)
            {
                return true;
            }

            if (TryParse(candidate, out var registeredUri)
                && IsLoopback(registeredUri)
                && IsLoopback(requestedUri)
                && registeredUri.Host == requestedUri.Host
                && registeredUri.AbsolutePath == requestedUri.AbsolutePath
                && registeredUri.Query == requestedUri.Query)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLoopback(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp && uri.Host is "localhost" or "127.0.0.1" && string.IsNullOrEmpty(uri.Fragment);

    private static bool TryParse(string value, out Uri uri)
    {
        var ok = Uri.TryCreate(value, UriKind.Absolute, out var parsed) && string.IsNullOrEmpty(parsed.Fragment) && string.IsNullOrEmpty(parsed.UserInfo);
        uri = parsed ?? new Uri("about:blank");
        return ok && parsed is not null && parsed.Scheme is "http" or "https";
    }
}
