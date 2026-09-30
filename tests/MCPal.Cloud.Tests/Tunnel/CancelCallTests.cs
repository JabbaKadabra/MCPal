using Autofac;
using MCPal.Cloud.Tests.Infrastructure;
using MCPal.Cloud.Tunnel;
using MCPal.Contracts;
using ModelContextProtocol.Client;

namespace MCPal.Cloud.Tests.Tunnel;

[TestFixture]
internal sealed class CancelCallTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static async Task<McpClient> ConnectAsync(CloudWebApplicationFactory factory, string apiKey)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(factory.Server.BaseAddress, "mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + apiKey },
            },
            factory.CreateClient(),
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static Func<CallToolRequest, Task<CallToolResponse>> NeverAnswers(TaskCompletionSource<string> started) => async request =>
    {
        started.TrySetResult(request.RequestId);
        await Task.Delay(TimeSpan.FromSeconds(60), Ct);
        return FakeAgent.Text("late");
    };

    [Test]
    public async Task CallTool_AgentDoesNotAnswerInTime_SendsCancelCallWithRequestId()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" });
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var started = new TaskCompletionSource<string>();
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "slow"), NeverAnswers(started), Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        var result = await client.CallToolAsync("kb__slow", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        var cancelled = await agent.NextCancelAsync(Ct);
        cancelled.Should().Be(await started.Task);
    }

    [Test]
    public async Task CallTool_HttpClientDisconnects_SendsCancelCallWithRequestId()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var started = new TaskCompletionSource<string>();
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "slow"), NeverAnswers(started), Ct);
        using var http = factory.CreateClient();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"kb__slow","arguments":{}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acme.RawKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var call = http.SendAsync(request, cancel.Token);
        var requestId = await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cancel.CancelAsync();

        (await agent.NextCancelAsync(Ct)).Should().Be(requestId);
        var act = async () => await call;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task CallTool_AgentSpeaksProtocol10_NeverGetsCancelCall()
    {
        var cancelled = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var factory = await CloudWebApplicationFactory.CreateAsync(
            Ct,
            settings: new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" },
            configureContainer: container => container.RegisterDecorator<IAgentInvoker>((_, _, inner) => new RecordingInvoker(inner, cancelled)));
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var started = new TaskCompletionSource<string>();
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWithProtocol("1.0", "kb", "slow"), NeverAnswers(started), Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        var result = await client.CallToolAsync("kb__slow", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        cancelled.Should().BeEmpty();
    }

    [Test]
    public async Task CallTool_AgentSpeaksProtocol11_InvokerReceivesCancelForTheConnection()
    {
        var cancelled = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var factory = await CloudWebApplicationFactory.CreateAsync(
            Ct,
            settings: new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" },
            configureContainer: container => container.RegisterDecorator<IAgentInvoker>((_, _, inner) => new RecordingInvoker(inner, cancelled)));
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var started = new TaskCompletionSource<string>();
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "slow"), NeverAnswers(started), Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__slow", cancellationToken: Ct);

        cancelled.Should().ContainSingle().Which.Should().Be(await started.Task);
    }

    private sealed class RecordingInvoker(IAgentInvoker inner, System.Collections.Concurrent.ConcurrentQueue<string> cancelled) : IAgentInvoker
    {
        public Task<CallToolResponse> CallToolAsync(string connectionId, CallToolRequest request, CancellationToken cancellationToken) =>
            inner.CallToolAsync(connectionId, request, cancellationToken);

        public Task CancelCallAsync(string connectionId, string requestId, CancellationToken cancellationToken)
        {
            cancelled.Enqueue(requestId);
            return Task.CompletedTask;
        }
    }
}
