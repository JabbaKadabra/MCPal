using System.Text.Json;
using MCPal.Agent.Concurrency;
using MCPal.Agent.Config;
using MCPal.Contracts;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MCPal.Agent.Local;

/// <summary>One local MCP server (stdio or HTTP). Starts lazily, recreates a dead client on the next use.</summary>
internal sealed class LocalServer(string name, LocalServerConfig config, ILoggerFactory loggerFactory, Action onToolsChanged) : IAsyncDisposable
{
    private readonly AsyncLock startLock = new();
    private readonly ILogger logger = loggerFactory.CreateLogger($"LocalServer.{name}");
    private McpClient? client;

    public string Name => name;

    public async Task<McpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        using (await startLock.AcquireAsync(cancellationToken))
        {
            if (client is { } existing)
            {
                if (!existing.Completion.IsCompleted)
                {
                    return existing;
                }

                logger.LogWarning("Local server '{Server}' stopped; restarting", name);
                await DisposeClientAsync();
            }

            var created = await McpClient.CreateAsync(CreateTransport(), cancellationToken: cancellationToken);
            created.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
            {
                onToolsChanged();
                return ValueTask.CompletedTask;
            });
            client = created;
            logger.LogInformation("Local server '{Server}' started", name);
            return created;
        }
    }

    public async Task<ServerCatalog> ListToolsAsync(CancellationToken cancellationToken)
    {
        var mcp = await GetClientAsync(cancellationToken);
        var tools = await mcp.ListToolsAsync(cancellationToken: cancellationToken);
        return new ServerCatalog(name, [.. tools.Select(ToDescriptor)]);
    }

    /// <summary>Drops the client so the next use starts a fresh one.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        using (await startLock.AcquireAsync(cancellationToken))
        {
            await DisposeClientAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync();
        startLock.Dispose();
    }

    private static ToolDescriptor ToDescriptor(McpClientTool tool) => new(
        tool.Name,
        tool.ProtocolTool.Title,
        tool.Description,
        tool.JsonSchema.GetRawText(),
        tool.ProtocolTool.Annotations is { } annotations ? JsonSerializer.Serialize(annotations, McpJsonUtilities.DefaultOptions) : null);

    private IClientTransport CreateTransport()
    {
        if (config.Command is { } command)
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = name,
                Command = command,
                ShutdownTimeout = TimeSpan.FromSeconds(1),
                Arguments = [.. config.Args],
                EnvironmentVariables = config.Env.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
            });
        }

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = name,
            Endpoint = new Uri(config.Url ?? throw new InvalidOperationException("Server has neither command nor url.")),
            AdditionalHeaders = config.Headers.ToDictionary(pair => pair.Key, pair => pair.Value),
        });
    }

    private async ValueTask DisposeClientAsync()
    {
        if (client is { } current)
        {
            client = null;
            try
            {
                await current.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ignoring error while disposing local server '{Server}'", name);
            }
        }
    }
}
