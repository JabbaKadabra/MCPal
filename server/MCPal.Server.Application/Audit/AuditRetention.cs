using MCPal.Server.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCPal.Server.Audit;

/// <summary>Deletes audit rows older than <see cref="McpalOptions.AuditRetentionDays"/>, in batches so no single delete locks the table for long.</summary>
internal sealed class AuditRetention(IMcpalData db, IOptions<McpalOptions> options, TimeProvider timeProvider)
{
    public const int BatchSize = 5000;

    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-options.Value.AuditRetentionDays);
        var total = 0;
        while (true)
        {
            var deleted = await db.Query<ToolCallAudit>()
                .Where(a => a.OccurredAt < cutoff)
                .OrderBy(a => a.OccurredAt)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            total += deleted;
            if (deleted < BatchSize)
            {
                return total;
            }
        }
    }
}
