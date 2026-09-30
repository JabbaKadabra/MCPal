using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using MCPal.Bridge.Concurrency;
using MCPal.Bridge.Config;
using MCPal.Bridge.Local;
using MCPal.Bridge.Status;
using MCPal.Contracts;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCPal.Bridge.Tunnel;

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

/// <summary>Who this bridge says it is when it registers. A record in the container so tests can present another protocol version.</summary>
internal sealed record BridgeInfo(string Version, string ProtocolVersion)
{
    public static BridgeInfo Current { get; } = new(
        typeof(BridgeInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        MCPal.Contracts.ProtocolVersion.Current);
}

/// <summary>
/// Keeps an outbound SignalR tunnel to the MCPal server: registers the local tools, answers tool calls and re-registers
/// after reconnects and tool list changes.
/// </summary>
internal sealed class TunnelClient : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>An incompatible bridge cannot fix itself by retrying; it only checks now and then whether the MCPal server was updated.</summary>
    private static readonly TimeSpan IncompatibleRetryInterval = TimeSpan.FromMinutes(15);

    private readonly BridgeConfig config;
    private readonly ILocalServerManager manager;
    private readonly BridgeStatusTracker status;
    private readonly BridgeInfo info;
    private readonly ITunnelTransportConfigurator transportConfigurator;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TunnelClient> logger;
    private readonly Channel<bool> changeSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly AsyncLock registerLock = new();
    /// <summary>Calls in progress, by request id, so a <c>CancelCall</c> from the MCPal server can stop them.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> runningCalls = new();
    /// <summary>Catalog the MCPal server fully accepted on this connection. Null while anything was rejected, so the refresh retries.</summary>
    private string? registeredFingerprint;
    private string? lastResult;

    /// <summary>The MCPal server rejected this bridge's protocol version. Volatile: read by the supervise loop, written by register calls.</summary>
    private volatile bool incompatible;

    public TunnelClient(
        BridgeConfig config,
        ILocalServerManager manager,
        BridgeStatusTracker status,
        BridgeInfo info,
        ITunnelTransportConfigurator transportConfigurator,
        TimeProvider timeProvider,
        ILogger<TunnelClient> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(transportConfigurator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.config = config;
        this.manager = manager;
        this.status = status;
        this.info = info;
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
            CancelAllCalls();
            return status.SetTunnelAsync(TunnelState.Reconnecting, stoppingToken);
        };
        connection.Closed += _ => status.SetTunnelAsync(TunnelState.Disconnected, CancellationToken.None);
        connection.Reconnected += async _ =>
        {
            logger.LogInformation("Tunnel reconnected");
            await status.SetTunnelAsync(TunnelState.Connected, stoppingToken);
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
        finally
        {
            await status.SetTunnelAsync(TunnelState.Disconnected, CancellationToken.None);
        }
    }

    private HubConnection BuildConnection(CancellationToken stoppingToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(config.Mcpal.Url + "/hub/bridge"), options =>
            {
                options.Headers["Authorization"] = "Bearer " + config.Mcpal.ApiKey;
                transportConfigurator.Configure(options);
            })
            .WithAutomaticReconnect(new ForeverBackoffPolicy())
            .Build();
        connection.On<CallToolRequest, CallToolResponse>(nameof(IBridgeHubClient.CallTool), request => RunCallAsync(request, stoppingToken));
        connection.On<string>(nameof(IBridgeHubClient.CancelCall), CancelCall);
        return connection;
    }

    private async Task<CallToolResponse> RunCallAsync(CallToolRequest request, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        runningCalls[request.RequestId] = cancellation;
        try
        {
            return await manager.CallToolAsync(request, cancellation.Token);
        }
        finally
        {
            runningCalls.TryRemove(request.RequestId, out _);
        }
    }

    /// <summary>Unknown ids are ignored: the call may just have finished.</summary>
    private void CancelCall(string requestId)
    {
        if (runningCalls.TryGetValue(requestId, out var cancellation))
        {
            logger.LogInformation("The MCPal server cancelled call {RequestId}", requestId);
            Cancel(cancellation);
        }
    }

    /// <summary>The MCPal server already failed every call of a lost tunnel, so nobody waits for their results.</summary>
    private void CancelAllCalls()
    {
        foreach (var cancellation in runningCalls.Values)
        {
            Cancel(cancellation);
        }
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call finished between lookup and cancel.
        }
    }

    private async Task ConnectAsync(HubConnection connection, CancellationToken stoppingToken)
    {
        for (var attempt = 0L; ; attempt++)
        {
            try
            {
                await connection.StartAsync(stoppingToken);
                await status.SetTunnelAsync(TunnelState.Connected, stoppingToken);
                await RegisterAsync(connection, force: true, stoppingToken);
                logger.LogInformation("Tunnel connected to {Url} as '{Bridge}'", config.Mcpal.Url, config.Mcpal.BridgeName);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                var delay = ForeverBackoffPolicy.Delay(attempt);
                logger.LogWarning("Cannot connect to {Url}: {Message}. Retrying in {Seconds}s", config.Mcpal.Url, ex.Message, delay.TotalSeconds);
                await connection.StopAsync(CancellationToken.None);
                await status.SetTunnelAsync(TunnelState.Disconnected, stoppingToken);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task SuperviseAsync(HubConnection connection, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var refresh = new CancellationTokenSource(incompatible ? IncompatibleRetryInterval : RefreshInterval, timeProvider);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refresh.Token);
            try
            {
                await changeSignals.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }

            await status.TouchAsync(stoppingToken);
            if (connection.State == HubConnectionState.Connected)
            {
                try
                {
                    await RegisterAsync(connection, force: false, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Could not update the tool list in the MCPal server: {Message}", ex.Message);
                }
            }
        }
    }

    /// <param name="force">
    /// True after (re)connecting: the MCPal server holds no catalog for a new connection. False for the refresh, which skips
    /// the call when the MCPal server already accepted this exact catalog.
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

            var catalog = new BridgeCatalog(config.Mcpal.BridgeName, info.Version, info.ProtocolVersion, servers);
            var result = await connection.InvokeAsync<RegisterResult>(nameof(IBridgeHubServer.Register), catalog, cancellationToken);

            // Rejections can be temporary (a server name still held by this bridge's previous, not yet timed out
            // connection), so only a clean result stops the refresh from registering again.
            var clean = result.Accepted && result.RejectedServers.Count == 0 && result.RejectedTools.Count == 0;
            registeredFingerprint = clean ? fingerprint : null;
            incompatible = result.Code == RegisterCodes.UnsupportedProtocol;
            LogResult(result, servers);
            await status.RegisteredAsync(servers, result, cancellationToken);
        }
    }

    private void LogResult(RegisterResult result, IReadOnlyList<ServerCatalog> servers)
    {
        // The refresh retries every 30 s while something is rejected; log an unchanged result only at debug level.
        var serialized = JsonSerializer.Serialize(result);
        var repeated = serialized == lastResult;
        lastResult = serialized;
        var level = repeated ? LogLevel.Debug : LogLevel.Error;

        if (result.Code == RegisterCodes.UnsupportedProtocol)
        {
            logger.Log(
                level,
                "This bridge (version {Version}, protocol {Protocol}) is too old or too new for the MCPal server: {Message} Download the matching bridge from {Url}. Trying again in {Minutes} minutes.",
                info.Version,
                info.ProtocolVersion,
                result.Message,
                config.Mcpal.Url,
                IncompatibleRetryInterval.TotalMinutes);
            return;
        }

        if (!result.Accepted)
        {
            logger.Log(level, "The MCPal server rejected this bridge: {Message}", result.Message);
            return;
        }

        foreach (var rejected in result.RejectedServers)
        {
            logger.Log(level, "The MCPal server rejected server '{Server}': {Reason}", rejected.ServerName, rejected.Reason);
        }

        foreach (var rejected in result.RejectedTools)
        {
            logger.Log(level, "The MCPal server rejected tool '{Tool}' of server '{Server}': {Reason}", rejected.ToolName, rejected.ServerName, rejected.Reason);
        }

        var rejectedServers = result.RejectedServers.Select(r => r.ServerName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = servers.Where(s => !rejectedServers.Contains(s.Name)).ToList();
        logger.Log(
            repeated ? LogLevel.Debug : LogLevel.Information,
            "Registered {Servers} server(s) with {Tools} tool(s)",
            registered.Count,
            registered.Sum(s => s.Tools.Count) - result.RejectedTools.Count);
    }

    /// <summary>Retries 1 s, 2 s, 4 s ... up to 60 s, forever.</summary>
    private sealed class ForeverBackoffPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delay(retryContext.PreviousRetryCount);

        public static TimeSpan Delay(long attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));
    }
}
