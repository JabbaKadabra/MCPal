using MCPal.Server.Tenancy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MCPal.Server.Access.UserContext;

/// <summary>
/// Issues the signed caller token that goes to a local MCP server with a tool call: who is calling, from which company, for
/// which server and tool. Short-lived (default 5 minutes), ES256, explicitly typed <c>mcpal-user+jwt</c> (RFC 8725).
/// </summary>
internal sealed class UserContextIssuer(SigningKeyStore keys, IOptions<McpalOptions> options, TimeProvider timeProvider)
{
    public const string TokenType = "mcpal-user+jwt";

    /// <summary>The audience of a token: the company and the server, so a token cannot be replayed at another company's or another server's endpoint.</summary>
    public static string Audience(string companySlug, string serverName) => $"mcpal:{companySlug}/{serverName}";

    /// <param name="AuthKind"><c>oauth</c> or <c>pat</c>: how the user signed in to MCPal.</param>
    /// <param name="RequestId">Becomes <c>jti</c>, so local servers can reject a replayed token.</param>
    public async Task<string> IssueAsync(
        UserPolicy user,
        Guid companyId,
        string companySlug,
        string serverName,
        string toolName,
        string requestId,
        string authKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var key = await keys.GetSigningKeyAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var email = user.Email;
        var descriptor = new SecurityTokenDescriptor
        {
            TokenType = TokenType,
            Issuer = options.Value.PublicUrl.TrimEnd('/'),
            Audience = Audience(companySlug, serverName),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddSeconds(options.Value.UserContext.TokenLifetimeSeconds),
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.EcdsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["jti"] = requestId,
                ["sub"] = user.UserId,
                ["email"] = email,
                ["name"] = string.IsNullOrWhiteSpace(user.DisplayName) ? email : user.DisplayName,
                ["groups"] = user.GroupNames.ToArray(),
                ["mcpal_role"] = user.IsOwner ? "owner" : "member",
                ["mcpal_company_id"] = companyId.ToString(),
                ["mcpal_company"] = companySlug,
                ["mcpal_tool"] = toolName,
                ["mcpal_auth"] = authKind,
            },
        };

        // The times come from the TimeProvider, not from the handler's own clock.
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }
}
