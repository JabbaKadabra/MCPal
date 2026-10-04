using MCPal.Server.Audit;
using MCPal.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MCPal.Server.Tests.Audit;

/// <summary>Builds and reads audit rows for tests.</summary>
internal static class AuditTestData
{
    public static ToolCallAudit Entry(
        Guid companyId,
        DateTimeOffset occurredAt,
        string tool = "search",
        string outcome = "ok",
        Guid? apiKeyId = null,
        string authKind = "pat",
        string? userId = null,
        string? oauthClientId = null,
        string server = "kb") => new()
    {
        Id = Guid.CreateVersion7(),
        CompanyId = companyId,
        OccurredAt = occurredAt,
        DurationMs = 12,
        AuthKind = authKind,
        UserId = userId,
        ApiKeyId = apiKeyId,
        OAuthClientId = oauthClientId,
        BridgeName = "hq-01",
        ServerName = server,
        ToolName = tool,
        PublicName = $"{server}__{tool}",
        Outcome = outcome,
    };

    public static async Task InsertAsync(IServiceProvider services, CancellationToken cancellationToken, params ToolCallAudit[] entries)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MCPalDbContext>();
        db.ToolCallAudits.AddRange(entries);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<List<ToolCallAudit>> ReadAsync(IServiceProvider services, Guid companyId, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MCPalDbContext>();
        return await db.ToolCallAudits.AsNoTracking().Where(a => a.CompanyId == companyId).OrderBy(a => a.OccurredAt).ToListAsync(cancellationToken);
    }

    /// <summary>The audit writer works off the request path, so a test waits until the rows arrived.</summary>
    public static async Task<List<ToolCallAudit>> WaitForRowsAsync(IServiceProvider services, Guid companyId, int count, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var rows = await ReadAsync(services, companyId, timeout.Token);
            if (rows.Count >= count)
            {
                return rows;
            }

            await Task.Delay(50, timeout.Token);
        }
    }
}
