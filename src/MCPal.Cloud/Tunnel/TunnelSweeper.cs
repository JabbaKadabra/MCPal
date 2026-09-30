using MCPal.Cloud.Tenancy;
using Microsoft.Extensions.Hosting;

namespace MCPal.Cloud.Tunnel;

/// <summary>
/// Closes tunnels whose key is no longer active. Revocation closes tunnels at once (<see cref="TunnelTerminator"/>);
/// key expiry and a disabled company have no such event, so they are caught here.
/// </summary>
internal sealed class TunnelSweeper(ConnectionRegistry registry, IApiKeyService apiKeys, ILogger<TunnelSweeper> logger)
{
    /// <summary>Returns the number of closed tunnels.</summary>
    public async Task<int> CloseInactiveAsync(CancellationToken cancellationToken)
    {
        var connections = registry.AllConnections();
        if (connections.Count == 0)
        {
            return 0;
        }

        var active = (await apiKeys.ActiveKeysAsync([.. connections.Select(c => c.ApiKeyId).Distinct()], cancellationToken)).ToHashSet();
        var closed = 0;
        foreach (var connection in connections.Where(c => !active.Contains(new ValidatedKey(c.CompanyId, c.ApiKeyId))))
        {
            registry.Remove(connection.CompanyId, connection.ConnectionId);
            connection.Abort();
            closed++;
            logger.LogInformation(
                "Closed tunnel {ConnectionId} of company {CompanyId}: its API key is expired, revoked or the company is disabled",
                connection.ConnectionId,
                connection.CompanyId);
        }

        return closed;
    }
}

/// <summary>Runs <see cref="TunnelSweeper"/> once a minute.</summary>
internal sealed class TunnelSweepService(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<TunnelSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TunnelSweeper>().CloseInactiveAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Tunnel sweep failed");
            }
        }
    }
}
