namespace MCPal.Bridge.Local;

/// <summary>
/// Holds the caller token of the tool call that is being sent on the current async flow. One instance per local HTTP server, so
/// there is no static state.
/// </summary>
internal sealed class UserTokenScope
{
    private readonly AsyncLocal<string?> current = new();

    public string? Current => current.Value;

    /// <summary>Makes <paramref name="token"/> the caller token until the returned scope is disposed.</summary>
    public IDisposable Enter(string? token)
    {
        var previous = current.Value;
        current.Value = token;
        return new Restore(current, previous);
    }

    private sealed class Restore(AsyncLocal<string?> current, string? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}

/// <summary>
/// Adds the caller token of the current tool call to requests to an HTTP local server, in the configured header
/// (<c>Authorization</c> becomes <c>Bearer &lt;token&gt;</c>). Outside a tool call's scope the header is removed, so other requests
/// of the same connection (the initial handshake, background streams, pings) never carry a user's token.
/// </summary>
internal sealed class UserTokenHeaderHandler(UserTokenScope scope, string headerName) : DelegatingHandler
{
    private const string Authorization = "Authorization";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Remove(headerName);
        if (scope.Current is { Length: > 0 } token)
        {
            request.Headers.TryAddWithoutValidation(headerName, string.Equals(headerName, Authorization, StringComparison.OrdinalIgnoreCase) ? "Bearer " + token : token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
