using MCPal.Server.Tenancy;

namespace MCPal.Server.Tunnel;

/// <summary>Closes all tunnels authenticated with a key once that key is revoked.</summary>
internal sealed class TunnelTerminator(ConnectionRegistry registry) : IApiKeyRevocationListener
{
    public Task OnRevokedAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken)
    {
        foreach (var connection in registry.ConnectionsForKey(companyId, apiKeyId))
        {
            registry.Remove(companyId, connection.ConnectionId);
            connection.Abort();
        }

        return Task.CompletedTask;
    }
}
