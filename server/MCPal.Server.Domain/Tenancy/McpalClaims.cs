using System.Security.Claims;

namespace MCPal.Server.Tenancy;

internal static class McpalClaims
{
    public const string CompanyId = "CompanyId";
    public const string UserId = "UserId";
    public const string ApiKeyId = "ApiKeyId";
    public const string OAuthClientId = "OAuthClientId";
    public const string KeyPurpose = "KeyPurpose";
    public const string AuthKind = "AuthKind";

    /// <summary>A bridge key (tunnels only).</summary>
    public const string AuthKindApiKey = "apikey";

    /// <summary>A personal access token.</summary>
    public const string AuthKindPat = "pat";
    public const string AuthKindOAuth = "oauth";

    /// <summary>The company of an authenticated principal. Null when the principal carries no valid company claim.</summary>
    public static Guid? GetCompanyId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirstValue(CompanyId), out var id) ? id : null;
    }

    /// <summary>The portal user behind a personal access token or OAuth token. Null for bridge keys.</summary>
    public static string? GetMcpalUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(UserId) is { Length: > 0 } id ? id : null;
    }

    public static Guid? GetApiKeyId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirstValue(ApiKeyId), out var id) ? id : null;
    }

    /// <summary>The purpose of the key behind this principal. Null when no key is involved (an OAuth token).</summary>
    public static ApiKeyPurpose? GetKeyPurpose(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Enum.TryParse<ApiKeyPurpose>(principal.FindFirstValue(KeyPurpose), out var purpose) ? purpose : null;
    }

    public static string? GetOAuthClientId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(OAuthClientId);
    }

    /// <summary>Who is calling. Null when the principal carries no valid company and user claim.</summary>
    public static CallerIdentity? GetCallerIdentity(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.GetCompanyId() is { } companyId && principal.GetMcpalUserId() is { } userId
            ? new CallerIdentity(companyId, userId, principal.FindFirstValue(AuthKind) ?? string.Empty, principal.GetApiKeyId(), principal.GetOAuthClientId())
            : null;
    }
}

/// <summary>The authenticated caller of an MCP request. Company and user come from the principal, never from the request.</summary>
internal sealed record CallerIdentity(Guid CompanyId, string UserId, string AuthKind, Guid? ApiKeyId, string? OAuthClientId);
