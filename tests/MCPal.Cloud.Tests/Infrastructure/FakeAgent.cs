using MCPal.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace MCPal.Cloud.Tests.Infrastructure;

/// <summary>A scripted agent: registers a catalog on the tunnel and answers tool calls with a delegate.</summary>
internal sealed class FakeAgent : IAsyncDisposable
{
    private readonly HubConnection connection;

    private FakeAgent(HubConnection connection)
    {
        this.connection = connection;
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> cancelledRequestIds = new();
    private readonly SemaphoreSlim cancelSignal = new(0);

    public RegisterResult? RegisterResult { get; private set; }

    /// <summary>Request ids the cloud sent <c>CancelCall</c> for, in arrival order.</summary>
    public IReadOnlyCollection<string> CancelledRequestIds => cancelledRequestIds;

    /// <summary>Waits for the next <c>CancelCall</c> message and returns its request id.</summary>
    public async Task<string> NextCancelAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await cancelSignal.WaitAsync(timeout.Token);
        return cancelledRequestIds.Last();
    }

    public static async Task<FakeAgent> StartAsync(
        CloudWebApplicationFactory factory,
        string apiKey,
        AgentCatalog catalog,
        Func<CallToolRequest, Task<CallToolResponse>> handler,
        CancellationToken cancellationToken)
    {
        var connection = factory.CreateAgentConnection(apiKey);
        connection.On<CallToolRequest, CallToolResponse>("CallTool", handler);
        var agent = new FakeAgent(connection);
        connection.On<string>("CancelCall", requestId =>
        {
            agent.cancelledRequestIds.Enqueue(requestId);
            agent.cancelSignal.Release();
        });
        await connection.StartAsync(cancellationToken);
        agent.RegisterResult = await connection.InvokeAsync<RegisterResult>("Register", catalog, cancellationToken);
        return agent;
    }

    public static AgentCatalog CatalogWith(string server, params string[] tools) => CatalogWithProtocol(ProtocolVersion.Current, server, tools);

    public static AgentCatalog CatalogWithProtocol(string protocolVersion, string server, params string[] tools) =>
        new("fake", "1.0", protocolVersion,
            [new ServerCatalog(server, [.. tools.Select(t => new ToolDescriptor(t, null, $"Tool {t}", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}", null))])]);

    public static CallToolResponse Text(string text) =>
        new(false, $"[{{\"type\":\"text\",\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}]", null);

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();
}
