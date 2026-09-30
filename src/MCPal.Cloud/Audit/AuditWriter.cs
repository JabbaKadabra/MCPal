using System.Threading.Channels;
using MCPal.Cloud.Diagnostics;
using MCPal.Cloud.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MCPal.Cloud.Audit;

/// <summary>Takes finished tool calls for the audit log without blocking the request.</summary>
internal interface IAuditSink
{
    void Enqueue(ToolCallAudit entry);
}

/// <summary>
/// Writes audit entries to the database off the request path. A bounded channel holds the entries; when it is full new
/// entries are dropped and counted (a slow database must not slow down or fail tool calls). A background loop inserts
/// whatever is queued, up to <see cref="MaxBatch"/> entries per insert, and drains the queue on shutdown.
/// </summary>
internal sealed class AuditWriter : BackgroundService, IAuditSink
{
    private const int MaxBatch = 500;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<ToolCallAudit> channel;
    private readonly IServiceScopeFactory scopes;
    private readonly CloudTelemetry telemetry;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AuditWriter> logger;
    private long dropped;

    public AuditWriter(IOptions<McpalOptions> options, IServiceScopeFactory scopes, CloudTelemetry telemetry, TimeProvider timeProvider, ILogger<AuditWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.scopes = scopes;
        this.telemetry = telemetry;
        this.timeProvider = timeProvider;
        this.logger = logger;
        channel = Channel.CreateBounded<ToolCallAudit>(
            new BoundedChannelOptions(options.Value.AuditQueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
            _ => CountDropped(1));
    }

    /// <summary>Entries lost because the queue was full or a database write failed.</summary>
    public long DroppedCount => Interlocked.Read(ref dropped);

    public void Enqueue(ToolCallAudit entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        entry.AuthKind = Truncate(entry.AuthKind, AuditColumns.AuthKind);
        entry.OAuthClientId = entry.OAuthClientId is null ? null : Truncate(entry.OAuthClientId, AuditColumns.OAuthClientId);
        entry.AgentName = Truncate(entry.AgentName, AuditColumns.Name);
        entry.ServerName = Truncate(entry.ServerName, AuditColumns.Name);
        entry.ToolName = Truncate(entry.ToolName, AuditColumns.Name);
        entry.PublicName = Truncate(entry.PublicName, AuditColumns.PublicName);
        entry.Outcome = Truncate(entry.Outcome, AuditColumns.Outcome);
        entry.ErrorMessage = entry.ErrorMessage is null ? null : Truncate(entry.ErrorMessage, AuditColumns.ErrorMessage);
        channel.Writer.TryWrite(entry);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync(stoppingToken))
            {
                await WriteNextBatchAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: drain below.
        }

        using var deadline = new CancellationTokenSource(DrainTimeout, timeProvider);
        try
        {
            while (channel.Reader.TryPeek(out _))
            {
                await WriteNextBatchAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Audit queue was not fully written before shutdown");
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private async Task WriteNextBatchAsync(CancellationToken cancellationToken)
    {
        var batch = new List<ToolCallAudit>(MaxBatch);
        while (batch.Count < MaxBatch && channel.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
        }

        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MCPalDbContext>();
            db.ToolCallAudits.AddRange(batch);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write {Count} audit entries", batch.Count);
            CountDropped(batch.Count);
        }
    }

    private void CountDropped(int count)
    {
        var total = Interlocked.Add(ref dropped, count);
        telemetry.RecordAuditDropped(count);

        // Once, then every thousand, so a stuck database does not flood the log.
        if (total - count < 1 || total / 1000 > (total - count) / 1000)
        {
            logger.LogWarning("Audit entries dropped so far: {Total}", total);
        }
    }
}
