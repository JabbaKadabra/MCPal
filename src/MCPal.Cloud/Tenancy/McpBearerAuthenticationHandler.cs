using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MCPal.Cloud.Tenancy;

internal static class McpBearerDefaults
{
    public const string Scheme = "McpBearer";

    /// <summary>Default authenticate scheme: bearer requests use <see cref="Scheme"/>, all others the portal cookie.</summary>
    public const string SelectorScheme = "McpBearerOrCookie";
    public const string TunnelPolicy = "Tunnel";
    public const string McpPolicy = "McpBearer";
}

/// <summary>
/// Authenticates <c>Authorization: Bearer</c> tokens. Values starting with <c>mcpal_</c> are API keys; anything else is an OAuth access token.
/// Tokens are never read from the query string.
/// </summary>
internal sealed class McpBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyService apiKeys,
    IAccessTokenValidator accessTokens,
    IOptions<McpalOptions> mcpalOptions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    public static bool HasBearer(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers.Authorization.ToString().StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!HasBearer(Request))
        {
            return AuthenticateResult.NoResult();
        }

        var token = Request.Headers.Authorization.ToString()[BearerPrefix.Length..].Trim();
        var claims = token.StartsWith(ApiKeyService.KeyPrefix, StringComparison.Ordinal)
            ? await AuthenticateApiKeyAsync(token)
            : await AuthenticateAccessTokenAsync(token);
        if (claims is null)
        {
            return AuthenticateResult.Fail("Invalid or expired credential.");
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var metadata = $"{mcpalOptions.Value.PublicUrl.TrimEnd('/')}/.well-known/oauth-protected-resource/mcp";
        var challenge = Request.Headers.Authorization.Count > 0
            ? $"Bearer error=\"invalid_token\", resource_metadata=\"{metadata}\""
            : $"Bearer resource_metadata=\"{metadata}\"";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = challenge;
        return Task.CompletedTask;
    }

    private async Task<Claim[]?> AuthenticateApiKeyAsync(string token)
    {
        var validated = await apiKeys.ValidateAsync(token, Context.RequestAborted);
        return validated is null
            ? null
            : [
                new Claim(McpalClaims.CompanyId, validated.CompanyId.ToString()),
                new Claim(McpalClaims.ApiKeyId, validated.ApiKeyId.ToString()),
                new Claim(McpalClaims.AuthKind, McpalClaims.AuthKindApiKey),
            ];
    }

    private async Task<Claim[]?> AuthenticateAccessTokenAsync(string token)
    {
        var validated = await accessTokens.ValidateAsync(token, Context.RequestAborted);
        if (validated is null)
        {
            return null;
        }

        var claims = new List<Claim>
        {
            new(McpalClaims.CompanyId, validated.CompanyId.ToString()),
            new(McpalClaims.AuthKind, McpalClaims.AuthKindOAuth),
        };
        if (validated.ApiKeyId is { } apiKeyId)
        {
            claims.Add(new Claim(McpalClaims.ApiKeyId, apiKeyId.ToString()));
        }

        return [.. claims];
    }
}
