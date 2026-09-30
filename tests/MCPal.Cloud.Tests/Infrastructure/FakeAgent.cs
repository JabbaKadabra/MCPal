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

    public RegisterResult? RegisterResult { get; private set; }

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
        await connection.StartAsync(cancellationToken);
        agent.RegisterResult = await connection.InvokeAsync<RegisterResult>("Register", catalog, cancellationToken);
        return agent;
    }

    public static AgentCatalog CatalogWith(string server, params string[] tools) =>
        new("fake", "1.0", ProtocolVersion.Current,
            [new ServerCatalog(server, [.. tools.Select(t => new ToolDescriptor(t, null, $"Tool {t}", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}", null))])]);

    public static CallToolResponse Text(string text) =>
        new(false, $"[{{\"type\":\"text\",\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}]", null);

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();
}
