using System.Security.Claims;

namespace MCPal.Cloud.Tenancy;

internal static class McpalClaims
{
    public const string CompanyId = "CompanyId";
    public const string ApiKeyId = "ApiKeyId";
    public const string OAuthClientId = "OAuthClientId";
    public const string KeyPurpose = "KeyPurpose";
    public const string AllowedServer = "AllowedServer";
    public const string AuthKind = "AuthKind";
    public const string AuthKindApiKey = "apikey";
    public const string AuthKindOAuth = "oauth";

    /// <summary>The company of an authenticated principal. Null when the principal carries no valid company claim.</summary>
    public static Guid? GetCompanyId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirstValue(CompanyId), out var id) ? id : null;
    }

    public static Guid? GetApiKeyId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirstValue(ApiKeyId), out var id) ? id : null;
    }

    /// <summary>The purpose of the key behind this principal. Null when no key is involved (a token issued from a portal session).</summary>
    public static ApiKeyPurpose? GetKeyPurpose(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Enum.TryParse<ApiKeyPurpose>(principal.FindFirstValue(KeyPurpose), out var purpose) ? purpose : null;
    }

    /// <summary>Servers the principal is restricted to. Empty means all servers.</summary>
    public static IReadOnlyList<string> GetAllowedServers(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return [.. principal.FindAll(AllowedServer).Select(c => c.Value)];
    }

    /// <summary>The claims that carry the restrictions of a key. Shared by the API key and OAuth token paths.</summary>
    public static IEnumerable<Claim> RestrictionClaims(ApiKeyPurpose purpose, IEnumerable<string> allowedServers) =>
        [new Claim(KeyPurpose, purpose.ToString()), .. allowedServers.Select(server => new Claim(AllowedServer, server))];

    public static string? GetOAuthClientId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(OAuthClientId);
    }

    /// <summary>Who is calling, for the audit log. Null when the principal carries no valid company claim.</summary>
    public static CallerIdentity? GetCallerIdentity(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.GetCompanyId() is { } companyId
            ? new CallerIdentity(companyId, principal.FindFirstValue(AuthKind) ?? string.Empty, principal.GetApiKeyId(), principal.GetOAuthClientId(), principal.GetAllowedServers())
            : null;
    }
}

/// <summary>The authenticated caller of an MCP request. The company comes from the principal, never from the request.</summary>
/// <param name="AllowedServers">Servers this caller may use; empty means all.</param>
internal sealed record CallerIdentity(Guid CompanyId, string AuthKind, Guid? ApiKeyId, string? OAuthClientId, IReadOnlyList<string> AllowedServers)
{
    /// <summary>Whether the caller may see and call tools of the server.</summary>
    public bool Allows(string serverName) =>
        AllowedServers.Count == 0 || AllowedServers.Contains(serverName, StringComparer.OrdinalIgnoreCase);
}
