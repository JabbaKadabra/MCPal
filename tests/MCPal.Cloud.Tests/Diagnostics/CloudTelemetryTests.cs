using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using MCPal.Cloud.Diagnostics;
using MCPal.Cloud.Tests.Infrastructure;
using MCPal.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using ModelContextProtocol.Client;

namespace MCPal.Cloud.Tests.Diagnostics;

[TestFixture]
internal sealed class CloudTelemetryTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static MetricCollector<T> Collect<T>(CloudWebApplicationFactory factory, string instrument)
        where T : struct =>
        new(factory.Services.GetRequiredService<IMeterFactory>(), CloudTelemetry.MeterName, instrument);

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

    /// <summary>The relay records the outcome right after it sent CancelCall, so the test may see the message first.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static string[] Outcomes(MetricCollector<long> collector) =>
        [.. collector.GetMeasurementSnapshot().Select(m => (string?)m.Tags["outcome"] ?? string.Empty)];

    [Test]
    public async Task CallTool_Success_CountsOkAndRecordsDuration()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var calls = Collect<long>(factory, "mcpal.tool_calls");
        var durations = Collect<double>(factory, "mcpal.tool_call.duration");
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "search"), _ => Task.FromResult(FakeAgent.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        Outcomes(calls).Should().Equal("ok");
        durations.GetMeasurementSnapshot().Should().ContainSingle().Which.Tags["outcome"].Should().Be("ok");
    }

    [Test]
    public async Task CallTool_AgentReportsError_CountsToolError()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var calls = Collect<long>(factory, "mcpal.tool_calls");
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "search"),
            _ => Task.FromResult(new CallToolResponse(true, "[]", "boom")), Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        Outcomes(calls).Should().Equal("tool_error");
    }

    [Test]
    public async Task CallTool_AgentTooSlow_CountsTimeout()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" });
        var calls = Collect<long>(factory, "mcpal.tool_calls");
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "slow"), async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), Ct);
            return FakeAgent.Text("late");
        }, Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__slow", cancellationToken: Ct);

        Outcomes(calls).Should().Equal("timeout");
    }

    [Test]
    public async Task CallTool_NoAgentOnline_CountsOffline()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var calls = Collect<long>(factory, "mcpal.tool_calls");
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        Outcomes(calls).Should().Equal("offline");
    }

    [Test]
    public async Task CallTool_AgentHandlerThrows_CountsRelayError()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var calls = Collect<long>(factory, "mcpal.tool_calls");
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "search"),
            _ => throw new InvalidOperationException("agent crashed"), Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        Outcomes(calls).Should().Equal("relay_error");
    }

    [Test]
    public async Task CallTool_HttpClientDisconnects_CountsCancelled()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var calls = Collect<long>(factory, "mcpal.tool_calls");
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var started = new TaskCompletionSource();
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "slow"), async _ =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), Ct);
            return FakeAgent.Text("late");
        }, Ct);
        using var http = factory.CreateClient();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"kb__slow","arguments":{}}}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acme.RawKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        var call = http.SendAsync(request, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await cancel.CancelAsync();
        await agent.NextCancelAsync(Ct);
        await WaitUntilAsync(() => calls.GetMeasurementSnapshot().Count > 0);

        Outcomes(calls).Should().Equal("cancelled");
        await call.Invoking(async c => await c).Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task CallTool_Metrics_NeverCarryCompanyOrToolAsTags()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var calls = Collect<long>(factory, "mcpal.tool_calls");
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "search"), _ => Task.FromResult(FakeAgent.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        calls.GetMeasurementSnapshot().Single().Tags.Keys.Should().Equal("outcome");
    }

    [Test]
    public async Task ActiveTunnels_AgentsConnected_GaugeShowsCount()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var tunnels = Collect<int>(factory, "mcpal.tunnels.active");
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var globex = await factory.SeedCompanyAsync("Globex", Ct);
        await using var first = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "a"), _ => Task.FromResult(FakeAgent.Text("x")), Ct);
        await using var second = await FakeAgent.StartAsync(factory, globex.RawKey, FakeAgent.CatalogWith("wiki", "b"), _ => Task.FromResult(FakeAgent.Text("x")), Ct);

        tunnels.RecordObservableInstruments();

        tunnels.LastMeasurement?.Value.Should().Be(2);
    }

    [Test]
    public async Task Register_AcceptedPartialAndRejected_CountedByResult()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var registrations = Collect<long>(factory, "mcpal.agent.registrations");
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var accepted = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "a"), _ => Task.FromResult(FakeAgent.Text("x")), Ct);
        await using var partial = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "a"), _ => Task.FromResult(FakeAgent.Text("x")), Ct);
        await using var rejected = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWithProtocol("99.0", "wiki", "b"), _ => Task.FromResult(FakeAgent.Text("x")), Ct);

        registrations.GetMeasurementSnapshot().Select(m => (string?)m.Tags["result"]).Should().Equal("accepted", "partial", "rejected");
    }

    [Test]
    public async Task Post_ExceedingRateLimit_CountsRejectionWithPolicy()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:McpRequestsPerMinute"] = "1" });
        var rejections = Collect<long>(factory, "mcpal.rate_limit.rejections");
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acme.RawKey);
            (await client.SendAsync(request, Ct)).Dispose();
        }

        rejections.GetMeasurementSnapshot().Should().NotBeEmpty().And.OnlyContain(m => (string?)m.Tags["policy"] == "mcp");
    }

    [Test]
    public async Task CallTool_Success_CreatesSpanWithTagsAndPassesTraceParentToAgent()
    {
        var spans = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CloudTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        CallToolRequest? seen = null;
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "search"), request =>
        {
            seen = request;
            return Task.FromResult(FakeAgent.Text("x"));
        }, Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        var span = spans.Single(a => a.OperationName == "mcpal.tool_call");

        span.GetTagItem("mcpal.server").Should().Be("kb");
        span.GetTagItem("mcpal.tool").Should().Be("search");
        span.GetTagItem("mcpal.outcome").Should().Be("ok");
        span.GetTagItem("mcpal.company_id").Should().Be(acme.CompanyId.ToString());
        seen?.TraceParent.Should().Be(span.Id);
    }
}
