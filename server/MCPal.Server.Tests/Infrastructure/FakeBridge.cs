using MCPal.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace MCPal.Server.Tests.Infrastructure;

/// <summary>A scripted bridge: registers a catalog on the tunnel and answers tool calls with a delegate.</summary>
internal sealed class FakeBridge : IAsyncDisposable
{
    private readonly HubConnection connection;

    private FakeBridge(HubConnection connection)
    {
        this.connection = connection;
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> cancelledRequestIds = new();
    private readonly SemaphoreSlim cancelSignal = new(0);

    public RegisterResult? RegisterResult { get; private set; }

    /// <summary>Request ids the server sent <c>CancelCall</c> for, in arrival order.</summary>
    public IReadOnlyCollection<string> CancelledRequestIds => cancelledRequestIds;

    /// <summary>Waits for the next <c>CancelCall</c> message and returns its request id.</summary>
    public async Task<string> NextCancelAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await cancelSignal.WaitAsync(timeout.Token);
        return cancelledRequestIds.Last();
    }

    public static async Task<FakeBridge> StartAsync(
        ServerWebApplicationFactory factory,
        string apiKey,
        BridgeCatalog catalog,
        Func<CallToolRequest, Task<CallToolResponse>> handler,
        CancellationToken cancellationToken)
    {
        var connection = factory.CreateBridgeConnection(apiKey);
        connection.On<CallToolRequest, CallToolResponse>("CallTool", handler);
        var bridge = new FakeBridge(connection);
        connection.On<string>("CancelCall", requestId =>
        {
            bridge.cancelledRequestIds.Enqueue(requestId);
            bridge.cancelSignal.Release();
        });
        await connection.StartAsync(cancellationToken);
        bridge.RegisterResult = await connection.InvokeAsync<RegisterResult>("Register", catalog, cancellationToken);
        return bridge;
    }

    public static BridgeCatalog CatalogWith(string server, params string[] tools) => CatalogWithProtocol(ProtocolVersion.Current, server, tools);

    public static BridgeCatalog CatalogWithProtocol(string protocolVersion, string server, params string[] tools) =>
        new("fake", "1.0", protocolVersion,
            [new ServerCatalog(server, [.. tools.Select(t => new ToolDescriptor(t, null, $"Tool {t}", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}", null))])]);

    public static CallToolResponse Text(string text) =>
        new(false, $"[{{\"type\":\"text\",\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}]", null);

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();
}
