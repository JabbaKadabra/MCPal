using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MCPal.Cloud.Storage;

/// <summary>Applies pending EF Core migrations at startup (<c>Mcpal:MigrateOnStartup</c>).</summary>
internal sealed class DatabaseMigrator(IServiceScopeFactory scopes, IOptions<McpalOptions> options, ILogger<DatabaseMigrator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.MigrateOnStartup)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MCPalDbContext>();
        logger.LogInformation("Applying database migrations");
        await db.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
