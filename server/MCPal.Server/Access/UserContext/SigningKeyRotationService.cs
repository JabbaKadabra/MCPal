using Microsoft.Extensions.Hosting;

namespace MCPal.Server.Access.UserContext;

/// <summary>Keeps the signing keys current: creates the first key at startup, pre-publishes successors and removes keys after their grace period, once an hour.</summary>
internal sealed class SigningKeyRotationService(SigningKeyStore store, TimeProvider timeProvider, ILogger<SigningKeyRotationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await store.RotateAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Signing key rotation failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
