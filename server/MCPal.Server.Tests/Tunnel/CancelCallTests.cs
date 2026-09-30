using Autofac;
using MCPal.Server.Tests.Infrastructure;
using MCPal.Server.Tunnel;
using MCPal.Contracts;
using ModelContextProtocol.Client;

namespace MCPal.Server.Tests.Tunnel;

[TestFixture]
internal sealed class CancelCallTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static async Task<McpClient> ConnectAsync(ServerWebApplicationFactory factory, string apiKey)
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
        return FakeBridge.Text("late");
    };

    [Test]
    public async Task CallTool_BridgeDoesNotAnswerInTime_SendsCancelCallWithRequestId()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" });
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var started = new TaskCompletionSource<string>();
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "slow"), NeverAnswers(started), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var result = await client.CallToolAsync("kb__slow", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        var cancelled = await bridge.NextCancelAsync(Ct);
        cancelled.Should().Be(await started.Task);
    }

    [Test]
    public async Task CallTool_HttpClientDisconnects_SendsCancelCallWithRequestId()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var started = new TaskCompletionSource<string>();
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "slow"), NeverAnswers(started), Ct);
        using var http = factory.CreateClient();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"kb__slow","arguments":{}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acme.PersonalKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var call = http.SendAsync(request, cancel.Token);
        var requestId = await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cancel.CancelAsync();

        (await bridge.NextCancelAsync(Ct)).Should().Be(requestId);
        var act = async () => await call;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task CallTool_BridgeSpeaksProtocol10_NeverGetsCancelCall()
    {
        var cancelled = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var factory = await ServerWebApplicationFactory.CreateAsync(
            Ct,
            settings: new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" },
            configureContainer: container => container.RegisterDecorator<IBridgeInvoker>((_, _, inner) => new RecordingInvoker(inner, cancelled)));
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var started = new TaskCompletionSource<string>();
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWithProtocol("1.0", "kb", "slow"), NeverAnswers(started), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var result = await client.CallToolAsync("kb__slow", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        cancelled.Should().BeEmpty();
    }

    [Test]
    public async Task CallTool_BridgeSpeaksProtocol11_InvokerReceivesCancelForTheConnection()
    {
        var cancelled = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var factory = await ServerWebApplicationFactory.CreateAsync(
            Ct,
            settings: new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" },
            configureContainer: container => container.RegisterDecorator<IBridgeInvoker>((_, _, inner) => new RecordingInvoker(inner, cancelled)));
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var started = new TaskCompletionSource<string>();
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "slow"), NeverAnswers(started), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        await client.CallToolAsync("kb__slow", cancellationToken: Ct);

        cancelled.Should().ContainSingle().Which.Should().Be(await started.Task);
    }

    private sealed class RecordingInvoker(IBridgeInvoker inner, System.Collections.Concurrent.ConcurrentQueue<string> cancelled) : IBridgeInvoker
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
