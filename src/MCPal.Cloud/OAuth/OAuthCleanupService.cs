using Microsoft.Extensions.Hosting;

namespace MCPal.Cloud.OAuth;

/// <summary>Removes expired authorization codes and tokens once a day.</summary>
internal sealed class OAuthCleanupService(IServiceScopeFactory scopes, ILogger<OAuthCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var removed = await scope.ServiceProvider.GetRequiredService<IOAuthService>().DeleteExpiredAsync(stoppingToken);
                logger.LogInformation("OAuth cleanup removed {Count} expired rows", removed);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "OAuth cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
