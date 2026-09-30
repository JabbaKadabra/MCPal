using MCPal.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.OAuth;

/// <summary>Revokes the OAuth tokens of a user, e.g. when the user is disabled or removed.</summary>
internal sealed class OAuthTokenRevoker(MCPalDbContext db)
{
    public async Task RevokeForUserAsync(Guid companyId, string userId, CancellationToken cancellationToken)
    {
        await db.OAuthTokens
            .Where(t => t.CompanyId == companyId && t.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Revoked, true), cancellationToken);
    }
}
