using System.Text.Json;
using System.Threading.Channels;
using MCPal.Agent.Concurrency;
using MCPal.Agent.Config;
using MCPal.Agent.Local;
using MCPal.Contracts;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCPal.Agent.Tunnel;

/// <summary>Lets hosts (tests) adjust the SignalR transport, e.g. to talk to an in-memory server.</summary>
internal interface ITunnelTransportConfigurator
{
    void Configure(HttpConnectionOptions options);
}

internal sealed class DefaultTunnelTransportConfigurator : ITunnelTransportConfigurator
{
    public void Configure(HttpConnectionOptions options)
    {
    }
}

/// <summary>
/// Keeps an outbound SignalR tunnel to the cloud: registers the local tools, answers tool calls and re-registers
/// after reconnects and tool list changes.
/// </summary>
internal sealed class TunnelClient : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly AgentConfig config;
    private readonly ILocalServerManager manager;
    private readonly ITunnelTransportConfigurator transportConfigurator;
    private readonly ILogger<TunnelClient> logger;
    private readonly Channel<bool> changeSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly AsyncLock registerLock = new();
    private string? registeredFingerprint;

    public TunnelClient(AgentConfig config, ILocalServerManager manager, ITunnelTransportConfigurator transportConfigurator, ILogger<TunnelClient> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(transportConfigurator);
        ArgumentNullException.ThrowIfNull(logger);

        this.config = config;
        this.manager = manager;
        this.transportConfigurator = transportConfigurator;
        this.logger = logger;
        manager.ToolsChanged += () => changeSignals.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = BuildConnection(stoppingToken);
        connection.Reconnecting += error =>
        {
            logger.LogWarning("Tunnel lost ({Message}); reconnecting", error?.Message ?? "no details");
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            logger.LogInformation("Tunnel reconnected");
            await RegisterAsync(connection, "Register", stoppingToken);
        };

        try
        {
            await ConnectAsync(connection, stoppingToken);
            await SuperviseAsync(connection, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Tunnel client stopping");
        }
    }

    private HubConnection BuildConnection(CancellationToken stoppingToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(config.Cloud.Url + "/hub/agent"), options =>
            {
                options.Headers["Authorization"] = "Bearer " + config.Cloud.ApiKey;
                transportConfigurator.Configure(options);
            })
            .WithAutomaticReconnect(new ForeverBackoffPolicy())
            .Build();
        connection.On<CallToolRequest, CallToolResponse>(nameof(IAgentHubClient.CallTool), request => manager.CallToolAsync(request, stoppingToken));
        return connection;
    }

    private async Task ConnectAsync(HubConnection connection, CancellationToken stoppingToken)
    {
        for (var attempt = 0L; ; attempt++)
        {
            try
            {
                await connection.StartAsync(stoppingToken);
                await RegisterAsync(connection, "Register", stoppingToken);
                logger.LogInformation("Tunnel connected to {Url} as '{Agent}'", config.Cloud.Url, config.Cloud.AgentName);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                var delay = ForeverBackoffPolicy.Delay(attempt);
                logger.LogWarning("Cannot connect to {Url}: {Message}. Retrying in {Seconds}s", config.Cloud.Url, ex.Message, delay.TotalSeconds);
                await connection.StopAsync(CancellationToken.None);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task SuperviseAsync(HubConnection connection, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(RefreshInterval);
            try
            {
                await changeSignals.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }

            if (connection.State == HubConnectionState.Connected)
            {
                try
                {
                    await RegisterAsync(connection, "ToolsChanged", stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Could not update the tool list in the cloud: {Message}", ex.Message);
                }
            }
        }
    }

    private async Task RegisterAsync(HubConnection connection, string method, CancellationToken cancellationToken)
    {
        using (await registerLock.AcquireAsync(cancellationToken))
        {
            var servers = await manager.ListServersAsync(cancellationToken);
            var fingerprint = JsonSerializer.Serialize(servers);
            if (method == "ToolsChanged" && fingerprint == registeredFingerprint)
            {
                return;
            }

            var catalog = new AgentCatalog(config.Cloud.AgentName, AgentVersion, ProtocolVersion.Current, servers);
            var result = await connection.InvokeAsync<RegisterResult>("Register", catalog, cancellationToken);
            registeredFingerprint = fingerprint;
            if (!result.Accepted)
            {
                logger.LogError("The cloud rejected this agent: {Message}", result.Message);
            }

            foreach (var rejected in result.RejectedServers)
            {
                logger.LogError("The cloud rejected server '{Server}': {Reason}", rejected.ServerName, rejected.Reason);
            }

            logger.LogInformation(
                "Registered {Servers} server(s) with {Tools} tool(s)",
                servers.Count,
                servers.Sum(s => s.Tools.Count));
        }
    }

    private static string AgentVersion => typeof(TunnelClient).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Retries 1 s, 2 s, 4 s ... up to 60 s, forever.</summary>
    private sealed class ForeverBackoffPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delay(retryContext.PreviousRetryCount);

        public static TimeSpan Delay(long attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));
    }
}
