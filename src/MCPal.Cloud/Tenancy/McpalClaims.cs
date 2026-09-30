using System.Security.Claims;

namespace MCPal.Cloud.Tenancy;

internal static class McpalClaims
{
    public const string CompanyId = "CompanyId";
    public const string ApiKeyId = "ApiKeyId";
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
}
