using System.Text.Json;
using MCPal.Agent.Config;
using MCPal.Contracts;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace MCPal.Agent.Local;

internal sealed class LocalServerManager : ILocalServerManager, IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, LocalServer> servers;
    private readonly TimeSpan callTimeout;
    private readonly ILogger<LocalServerManager> logger;

    public LocalServerManager(AgentConfig config, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        callTimeout = TimeSpan.FromSeconds(config.CallTimeoutSeconds);
        logger = loggerFactory.CreateLogger<LocalServerManager>();
        servers = config.McpServers.ToDictionary(
            pair => pair.Key,
            pair => new LocalServer(pair.Key, pair.Value, loggerFactory, () => ToolsChanged?.Invoke()));
    }

    public event Action? ToolsChanged;

    public async Task<IReadOnlyList<ServerCatalog>> ListServersAsync(CancellationToken cancellationToken)
    {
        var catalogs = await Task.WhenAll(servers.Values.Select(server => TryListAsync(server, cancellationToken)));
        return [.. catalogs.OfType<ServerCatalog>()];
    }

    public async Task<CallToolResponse> CallToolAsync(CallToolRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!servers.TryGetValue(request.ServerName, out var server))
        {
            return Failure($"Unknown server '{request.ServerName}'.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(callTimeout);
        try
        {
            var arguments = string.IsNullOrWhiteSpace(request.ArgumentsJson)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.ArgumentsJson, McpJsonUtilities.DefaultOptions);
            var client = await server.GetClientAsync(timeout.Token);
            var result = await client.CallToolAsync(
                new CallToolRequestParams { Name = request.ToolName, Arguments = arguments },
                timeout.Token);
            return new CallToolResponse(result.IsError == true, JsonSerializer.Serialize(result.Content, McpJsonUtilities.DefaultOptions), null);
        }
        catch (Exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Call {Server}/{Tool} timed out after {Seconds}s", request.ServerName, request.ToolName, callTimeout.TotalSeconds);
            await ResetQuietlyAsync(server);
            return Failure($"Tool call timed out after {callTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Call {Server}/{Tool} failed", request.ServerName, request.ToolName);
            return Failure($"Local server '{request.ServerName}' failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in servers.Values)
        {
            await server.DisposeAsync();
        }
    }

    private static CallToolResponse Failure(string message) => new(true, "[]", message);

    private async Task<ServerCatalog?> TryListAsync(LocalServer server, CancellationToken cancellationToken)
    {
        try
        {
            return await server.ListToolsAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Local server '{Server}' is not available: {Message}", server.Name, ex.Message);
            await ResetQuietlyAsync(server);
            return null;
        }
    }

    private static async Task ResetQuietlyAsync(LocalServer server)
    {
        await server.ResetAsync(CancellationToken.None);
    }
}
