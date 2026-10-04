using MCPal.Server.Ports;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.OAuth;

/// <summary>Revokes the OAuth tokens of a user, e.g. when the user is disabled or removed.</summary>
internal sealed class OAuthTokenRevoker(IMcpalData db)
{
    public async Task RevokeForUserAsync(Guid companyId, string userId, CancellationToken cancellationToken)
    {
        await db.Query<OAuthToken>()
            .Where(t => t.CompanyId == companyId && t.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Revoked, true), cancellationToken);
    }
}
