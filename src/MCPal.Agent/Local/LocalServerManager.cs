using System.Text.Json;
using MCPal.Agent.Config;
using MCPal.Contracts;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MCPal.Agent.Local;

internal sealed class LocalServerManager : ILocalServerManager, IAsyncDisposable
{
    private const string ServerInfoMetaKey = "io.modelcontextprotocol/serverInfo";

    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);

    private readonly IReadOnlyDictionary<string, LocalServer> servers;
    private readonly AgentTelemetry telemetry;
    private readonly TimeSpan callTimeout;
    private readonly ILogger<LocalServerManager> logger;

    public LocalServerManager(AgentConfig config, ILoggerFactory loggerFactory, AgentTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        this.telemetry = telemetry;
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        callTimeout = TimeSpan.FromSeconds(config.CallTimeoutSeconds);
        logger = loggerFactory.CreateLogger<LocalServerManager>();
        servers = config.McpServers.ToDictionary(
            pair => pair.Key,
            pair => new LocalServer(pair.Key, pair.Value, loggerFactory, () => ToolsChanged?.Invoke()));
    }

    public event Action? ToolsChanged;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> HiddenTools =>
        servers.ToDictionary(pair => pair.Key, pair => pair.Value.HiddenTools);

    public async Task<IReadOnlyList<ServerCatalog>> ListServersAsync(CancellationToken cancellationToken)
    {
        var catalogs = await Task.WhenAll(servers.Values.Select(server => TryListAsync(server, cancellationToken)));
        return [.. catalogs.OfType<ServerCatalog>()];
    }

    public async Task<CallToolResponse> CallToolAsync(CallToolRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = telemetry.StartLocalCall(request);
        var (response, outcome) = await ExecuteAsync(request, cancellationToken);
        activity?.SetTag("mcpal.outcome", outcome);
        if (outcome is "failed" or "timeout")
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
        }

        return response;
    }

    private async Task<(CallToolResponse Response, string Outcome)> ExecuteAsync(CallToolRequest request, CancellationToken cancellationToken)
    {
        if (!servers.TryGetValue(request.ServerName, out var server))
        {
            return (Failure($"Unknown server '{request.ServerName}'."), "failed");
        }

        // The cloud only resolves registered tools, but the agent must not rely on that.
        if (!server.IsExposed(request.ToolName))
        {
            return (Failure($"Tool '{request.ToolName}' is not exposed by this agent."), "failed");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(callTimeout);
        McpClient? client = null;
        var requestId = new RequestId(Guid.NewGuid().ToString("N"));
        try
        {
            var arguments = string.IsNullOrWhiteSpace(request.ArgumentsJson)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.ArgumentsJson, McpJsonUtilities.DefaultOptions);
            client = await server.GetClientAsync(timeout.Token);
            var result = await client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
                RequestMethods.ToolsCall,
                new CallToolRequestParams { Name = request.ToolName, Arguments = arguments },
                McpJsonUtilities.DefaultOptions,
                requestId,
                timeout.Token);
            var response = new CallToolResponse(
                result.IsError == true,
                JsonSerializer.Serialize(result.Content, McpJsonUtilities.DefaultOptions),
                null,
                result.StructuredContent is { } structured ? structured.GetRawText() : null,
                ToolMetaJson(result.Meta));
            return (response, response.IsError ? "tool_error" : "ok");
        }
        catch (Exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Call {Server}/{Tool} timed out after {Seconds}s", request.ServerName, request.ToolName, callTimeout.TotalSeconds);
            await NotifyCancelledAsync(client, requestId, "timeout");
            await ResetWhenUnresponsiveAsync(server);
            return (Failure($"Tool call timed out after {callTimeout.TotalSeconds:0} s."), "timeout");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up (the cloud timed out, the client left or the tunnel dropped). Only this call is
            // cancelled at the local server; other calls on the same server keep running.
            logger.LogInformation("Call {Server}/{Tool} was cancelled", request.ServerName, request.ToolName);
            await NotifyCancelledAsync(client, requestId, "cancelled by the cloud");
            return (Failure("Tool call was cancelled."), "cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Call {Server}/{Tool} failed", request.ServerName, request.ToolName);
            return (Failure($"Local server '{request.ServerName}' failed: {ex.Message}"), "failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in servers.Values)
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// The SDK stamps every result of a server with its own <c>serverInfo</c>. That describes the local server, not the
    /// cloud endpoint Claude talks to (the cloud stamps its own), so only the tool's own <c>_meta</c> passes through.
    /// </summary>
    private static string? ToolMetaJson(System.Text.Json.Nodes.JsonObject? meta)
    {
        if (meta is null)
        {
            return null;
        }

        var own = new System.Text.Json.Nodes.JsonObject(meta.Where(pair => pair.Key != ServerInfoMetaKey).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
        return own.Count == 0 ? null : own.ToJsonString(McpJsonUtilities.DefaultOptions);
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

    /// <summary>
    /// Tells the local server to stop the call (<c>notifications/cancelled</c>). The MCP client does not send it when the
    /// caller's token fires, so the local tool would run to its end. Best effort: a dead server needs no notification.
    /// </summary>
    private async Task NotifyCancelledAsync(McpClient? client, RequestId requestId, string reason)
    {
        if (client is null)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(PingTimeout);
        try
        {
            await client.SendNotificationAsync(
                NotificationMethods.CancelledNotification,
                new CancelledNotificationParams { RequestId = requestId, Reason = reason },
                McpJsonUtilities.DefaultOptions,
                deadline.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not send the cancellation to the local server: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// After a timeout the server may still work on other calls. A reset kills a stdio process and all its in-flight
    /// calls, so it only happens when the server no longer answers a ping.
    /// </summary>
    private async Task ResetWhenUnresponsiveAsync(LocalServer server)
    {
        try
        {
            using var pingTimeout = new CancellationTokenSource(PingTimeout);
            var client = await server.GetClientAsync(pingTimeout.Token);
            await client.PingAsync(cancellationToken: pingTimeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Local server '{Server}' does not answer after a timeout ({Message}); restarting it", server.Name, ex.Message);
            await ResetQuietlyAsync(server);
        }
    }

    private static async Task ResetQuietlyAsync(LocalServer server)
    {
        await server.ResetAsync(CancellationToken.None);
    }
}
