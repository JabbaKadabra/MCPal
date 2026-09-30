using Microsoft.Extensions.Hosting;

namespace MCPal.Cloud.Audit;

/// <summary>Applies the audit retention once an hour.</summary>
internal sealed class AuditCleanupService(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<AuditCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var removed = await scope.ServiceProvider.GetRequiredService<AuditRetention>().DeleteExpiredAsync(stoppingToken);
                logger.LogInformation("Audit cleanup removed {Count} expired rows", removed);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Audit cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
