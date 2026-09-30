using MCPal.Agent.Concurrency;
using MCPal.Agent.Config;
using MCPal.Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace MCPal.Agent.Status;

/// <summary>
/// Keeps the agent's state and writes it to <c>statusFile</c> (when configured) on every change, so monitoring tools
/// on the machine can read it. A temp file plus rename means readers never see a partial file.
/// Writing is best effort: a full disk or a wrong path must not stop the tunnel.
/// </summary>
internal sealed class AgentStatusTracker(AgentConfig config, TimeProvider timeProvider, ILoggerFactory loggerFactory) : IDisposable
{
    private readonly ILogger logger = loggerFactory.CreateLogger<AgentStatusTracker>();
    private readonly AsyncLock gate = new();
    private TunnelState tunnel = TunnelState.Connecting;
    private DateTimeOffset? lastRegisteredAt;
    private IReadOnlyList<ServerStatus> servers = [.. config.McpServers.Keys.Select(name => new ServerStatus(name, "starting", 0))];
    private IReadOnlyList<RejectedStatus> rejected = [];

    public async Task SetTunnelAsync(TunnelState state, CancellationToken cancellationToken)
    {
        using (await gate.AcquireAsync(cancellationToken))
        {
            tunnel = state;
            await WriteAsync(cancellationToken);
        }
    }

    /// <summary>Records the result of a <c>Register</c> call for the given local catalogs.</summary>
    public async Task RegisteredAsync(IReadOnlyList<ServerCatalog> catalogs, RegisterResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(result);

        var problems = new List<RejectedStatus>();
        if (!result.Accepted)
        {
            problems.Add(new RejectedStatus("*", result.Message ?? "The cloud rejected this agent."));
        }

        problems.AddRange(result.RejectedServers.Select(r => new RejectedStatus(r.ServerName, r.Reason)));
        problems.AddRange(result.RejectedTools.Select(r => new RejectedStatus(r.ServerName, $"tool '{r.ToolName}': {r.Reason}")));

        var rejectedServers = result.RejectedServers.Select(r => r.ServerName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejectedToolCounts = result.RejectedTools.GroupBy(r => r.ServerName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var started = catalogs.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var statuses = new List<ServerStatus>();
        foreach (var name in config.McpServers.Keys.Union(started.Keys, StringComparer.Ordinal))
        {
            if (!started.TryGetValue(name, out var catalog))
            {
                statuses.Add(new ServerStatus(name, "failed", 0));
            }
            else if (!result.Accepted || rejectedServers.Contains(name))
            {
                statuses.Add(new ServerStatus(name, "rejected", 0));
            }
            else
            {
                statuses.Add(new ServerStatus(name, "running", catalog.Tools.Count - rejectedToolCounts.GetValueOrDefault(name)));
            }
        }

        using (await gate.AcquireAsync(cancellationToken))
        {
            servers = statuses;
            rejected = problems;
            lastRegisteredAt = timeProvider.GetUtcNow();
            await WriteAsync(cancellationToken);
        }
    }

    /// <summary>Writes the file again without changing the state, so <c>updatedAt</c> shows that the agent is alive.</summary>
    public async Task TouchAsync(CancellationToken cancellationToken)
    {
        using (await gate.AcquireAsync(cancellationToken))
        {
            await WriteAsync(cancellationToken);
        }
    }

    public void Dispose() => gate.Dispose();

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.StatusFile))
        {
            return;
        }

        var status = new AgentStatus(
            timeProvider.GetUtcNow(),
            tunnel.ToString().ToLowerInvariant(),
            config.Cloud.Url,
            lastRegisteredAt,
            servers,
            rejected);
        var path = Path.GetFullPath(config.StatusFile);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(status, AgentStatusFile.JsonOptions), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Cannot write the status file '{Path}': {Message}", path, ex.Message);
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }
}
