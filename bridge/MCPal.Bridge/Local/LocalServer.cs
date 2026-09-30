using System.Text.Json;
using MCPal.Bridge.Concurrency;
using MCPal.Bridge.Config;
using MCPal.Contracts;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MCPal.Bridge.Local;

/// <summary>One local MCP server (stdio or HTTP). Starts lazily, recreates a dead client on the next use.</summary>
internal sealed class LocalServer(string name, LocalServerConfig config, ILoggerFactory loggerFactory, Action onToolsChanged) : IAsyncDisposable
{
    private readonly AsyncLock startLock = new();
    private readonly ILogger logger = loggerFactory.CreateLogger($"LocalServer.{name}");
    private readonly ToolFilter filter = new(config.IncludeTools, config.ExcludeTools);
    private readonly UserTokenScope? tokenScope = config.UserTokenHeader is null ? null : new UserTokenScope();
    private McpClient? client;

    public string Name => name;

    /// <summary>Whether this server gets the caller of each tool call (<c>userContext</c> in the config).</summary>
    public bool SendsUserContext => config.UserContext;

    /// <summary>
    /// Makes <paramref name="token"/> the caller token of requests sent on this async flow until the scope ends. Only tool calls
    /// enter it, and only after the client exists, so connection setup and background streams never carry a user's token.
    /// Without a configured header it does nothing.
    /// </summary>
    public IDisposable EnterUserScope(string? token) => tokenScope?.Enter(token) ?? NoScope.Instance;

    /// <summary>Names of tools the last listing hid because of <c>includeTools</c> / <c>excludeTools</c>.</summary>
    public IReadOnlyList<string> HiddenTools { get; private set; } = [];

    public bool IsExposed(string toolName) => filter.IsExposed(toolName);

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
        HiddenTools = [.. tools.Where(tool => !filter.IsExposed(tool.Name)).Select(tool => tool.Name)];
        return new ServerCatalog(name, [.. tools.Where(tool => filter.IsExposed(tool.Name)).Select(ToDescriptor)]);
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
        tool.ProtocolTool.Annotations is { } annotations ? JsonSerializer.Serialize(annotations, McpJsonUtilities.DefaultOptions) : null,
        tool.ProtocolTool.OutputSchema is { } outputSchema ? outputSchema.GetRawText() : null);

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

        var options = new HttpClientTransportOptions
        {
            Name = name,
            Endpoint = new Uri(config.Url ?? throw new InvalidOperationException("Server has neither command nor url.")),
            AdditionalHeaders = config.Headers.ToDictionary(pair => pair.Key, pair => pair.Value),
        };
        if (tokenScope is null || config.UserTokenHeader is not { } header)
        {
            return new HttpClientTransport(options);
        }

        // Own HttpClient: its handler adds the caller token to the requests of a tool call and to no others.
        // No client timeout: the bridge limits every call itself (callTimeoutSeconds), and streams stay open on purpose.
        var http = new HttpClient(new UserTokenHeaderHandler(tokenScope, header) { InnerHandler = new HttpClientHandler() }) { Timeout = Timeout.InfiniteTimeSpan };
        return new HttpClientTransport(options, http, loggerFactory, ownsHttpClient: true);
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
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
