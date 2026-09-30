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
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TunnelClient> logger;
    private readonly Channel<bool> changeSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly AsyncLock registerLock = new();
    /// <summary>Catalog the cloud fully accepted on this connection. Null while anything was rejected, so the refresh retries.</summary>
    private string? registeredFingerprint;
    private string? lastResult;

    public TunnelClient(
        AgentConfig config,
        ILocalServerManager manager,
        ITunnelTransportConfigurator transportConfigurator,
        TimeProvider timeProvider,
        ILogger<TunnelClient> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(transportConfigurator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.config = config;
        this.manager = manager;
        this.transportConfigurator = transportConfigurator;
        this.timeProvider = timeProvider;
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
            await RegisterAsync(connection, force: true, stoppingToken);
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
                await RegisterAsync(connection, force: true, stoppingToken);
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
            using var refresh = new CancellationTokenSource(RefreshInterval, timeProvider);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refresh.Token);
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
                    await RegisterAsync(connection, force: false, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Could not update the tool list in the cloud: {Message}", ex.Message);
                }
            }
        }
    }

    /// <param name="force">
    /// True after (re)connecting: the cloud holds no catalog for a new connection. False for the refresh, which skips
    /// the call when the cloud already accepted this exact catalog.
    /// </param>
    private async Task RegisterAsync(HubConnection connection, bool force, CancellationToken cancellationToken)
    {
        using (await registerLock.AcquireAsync(cancellationToken))
        {
            var servers = await manager.ListServersAsync(cancellationToken);
            var fingerprint = JsonSerializer.Serialize(servers);
            if (!force && fingerprint == registeredFingerprint)
            {
                return;
            }

            var catalog = new AgentCatalog(config.Cloud.AgentName, AgentVersion, ProtocolVersion.Current, servers);
            var result = await connection.InvokeAsync<RegisterResult>(nameof(IAgentHubServer.Register), catalog, cancellationToken);

            // Rejections can be temporary (a server name still held by this agent's previous, not yet timed out
            // connection), so only a clean result stops the refresh from registering again.
            var clean = result.Accepted && result.RejectedServers.Count == 0 && result.RejectedTools.Count == 0;
            registeredFingerprint = clean ? fingerprint : null;
            LogResult(result, servers);
        }
    }

    private void LogResult(RegisterResult result, IReadOnlyList<ServerCatalog> servers)
    {
        // The refresh retries every 30 s while something is rejected; log an unchanged result only at debug level.
        var serialized = JsonSerializer.Serialize(result);
        var repeated = serialized == lastResult;
        lastResult = serialized;
        var level = repeated ? LogLevel.Debug : LogLevel.Error;

        if (!result.Accepted)
        {
            logger.Log(level, "The cloud rejected this agent: {Message}", result.Message);
            return;
        }

        foreach (var rejected in result.RejectedServers)
        {
            logger.Log(level, "The cloud rejected server '{Server}': {Reason}", rejected.ServerName, rejected.Reason);
        }

        foreach (var rejected in result.RejectedTools)
        {
            logger.Log(level, "The cloud rejected tool '{Tool}' of server '{Server}': {Reason}", rejected.ToolName, rejected.ServerName, rejected.Reason);
        }

        var rejectedServers = result.RejectedServers.Select(r => r.ServerName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = servers.Where(s => !rejectedServers.Contains(s.Name)).ToList();
        logger.Log(
            repeated ? LogLevel.Debug : LogLevel.Information,
            "Registered {Servers} server(s) with {Tools} tool(s)",
            registered.Count,
            registered.Sum(s => s.Tools.Count) - result.RejectedTools.Count);
    }

    private static string AgentVersion => typeof(TunnelClient).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Retries 1 s, 2 s, 4 s ... up to 60 s, forever.</summary>
    private sealed class ForeverBackoffPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delay(retryContext.PreviousRetryCount);

        public static TimeSpan Delay(long attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));
    }
}
