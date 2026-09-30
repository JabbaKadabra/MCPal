using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.OAuth;

/// <summary>Revoking an API key also revokes every OAuth token issued from it.</summary>
internal sealed class OAuthTokenRevoker(MCPalDbContext db) : IApiKeyRevocationListener
{
    public async Task OnRevokedAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken)
    {
        await db.OAuthTokens
            .Where(t => t.CompanyId == companyId && t.ApiKeyId == apiKeyId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Revoked, true), cancellationToken);
    }
}
