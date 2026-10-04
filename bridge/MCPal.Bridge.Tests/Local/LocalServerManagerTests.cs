using System.Text.Json;
using Autofac;
using MCPal.Bridge.Config;
using MCPal.Bridge.Local;
using MCPal.Bridge.Tests.Infrastructure;
using MCPal.Contracts;

namespace MCPal.Bridge.Tests.Local;

[TestFixture]
internal sealed class LocalServerManagerTests : BridgeTestBase
{
    private static CallToolRequest Request(string server, string tool, string argumentsJson = "{}") =>
        new(Guid.NewGuid().ToString("N"), server, tool, argumentsJson);

    private static string TextOf(CallToolResponse response)
    {
        using var document = JsonDocument.Parse(response.ContentJson);
        return document.RootElement[0].GetProperty("text").GetString() ?? string.Empty;
    }

    [Test]
    public async Task ListServersAsync_StdioServer_ReturnsAllToolsWithSchema()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var catalogs = await scope.Resolve<ILocalServerManager>().ListServersAsync(Ct);

        var server = catalogs.Should().ContainSingle().Which;
        server.Name.Should().Be("test");
        server.Tools.Select(t => t.Name).Should().Contain(["echo", "add", "slow", "fail"]);
        server.Tools.Single(t => t.Name == "echo").InputSchemaJson.Should().Contain("text");
    }

    [Test]
    public async Task CallToolAsync_Echo_ReturnsContentJson()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "echo", """{"text":"hi"}"""), Ct);

        response.IsError.Should().BeFalse();
        TextOf(response).Should().Be("echo: hi");
    }

    [Test]
    public async Task ListServersAsync_ToolWithOutputSchema_ReturnsItAsJson()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var catalogs = await scope.Resolve<ILocalServerManager>().ListServersAsync(Ct);

        var tools = catalogs.Single().Tools;
        using var schema = JsonDocument.Parse(tools.Single(t => t.Name == "weather").OutputSchemaJson ?? "null");
        schema.RootElement.GetProperty("properties").TryGetProperty("temperature", out _).Should().BeTrue();
        tools.Single(t => t.Name == "echo").OutputSchemaJson.Should().BeNull();
    }

    [Test]
    public async Task CallToolAsync_ToolWithStructuredContent_ReturnsItAsJson()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "weather", """{"city":"Linz"}"""), Ct);

        using var structured = JsonDocument.Parse(response.StructuredContentJson ?? "null");
        structured.RootElement.GetProperty("city").GetString().Should().Be("Linz");
        structured.RootElement.GetProperty("temperature").GetDouble().Should().Be(21.5);
    }

    [Test]
    public async Task CallToolAsync_ToolWithoutStructuredContent_LeavesItNull()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "echo", """{"text":"x"}"""), Ct);

        response.StructuredContentJson.Should().BeNull();
        response.MetaJson.Should().BeNull();
    }

    [Test]
    public async Task CallToolAsync_ToolWithOwnMeta_ReturnsItWithoutTheSdkServerInfo()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "with_meta"), Ct);

        using var meta = JsonDocument.Parse(response.MetaJson ?? "null");
        meta.RootElement.GetProperty("source").GetString().Should().Be("station-7");
        meta.RootElement.TryGetProperty("io.modelcontextprotocol/serverInfo", out _).Should().BeFalse();
    }

    [Test]
    public async Task CallToolAsync_RequestWithTraceParent_StartsLocalCallSpanAsChildOfIt()
    {
        var spans = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "MCPal.Bridge",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = spans.Enqueue,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));
        const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var request = new CallToolRequest(Guid.NewGuid().ToString("N"), "test", "echo", """{"text":"hi"}""", traceParent);

        await scope.Resolve<ILocalServerManager>().CallToolAsync(request, Ct);

        var span = spans.Single(a => a.OperationName == "mcpal.local_call");
        span.ParentId.Should().Be(traceParent);
        span.TraceId.ToString().Should().Be("0af7651916cd43dd8448eb211c80319c");
        span.GetTagItem("mcpal.server").Should().Be("test");
        span.GetTagItem("mcpal.tool").Should().Be("echo");
        span.GetTagItem("mcpal.outcome").Should().Be("ok");
    }

    [Test]
    public async Task CallToolAsync_RequestWithoutTraceParent_StillWorks()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "echo", """{"text":"hi"}"""), Ct);

        response.IsError.Should().BeFalse();
    }

    [Test]
    public async Task CallToolAsync_ToolThrows_ReturnsIsError()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "fail"), Ct);

        response.IsError.Should().BeTrue();
    }

    [Test]
    public async Task CallToolAsync_UnknownServer_ReturnsIsErrorWithoutThrowing()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("nope", "echo"), Ct);

        response.IsError.Should().BeTrue();
        response.ErrorMessage.Should().Contain("nope");
    }

    [Test]
    public async Task CallToolAsync_ToolTooSlow_ReturnsTimeoutError()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }, callTimeoutSeconds: 1));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "slow", """{"milliseconds":10000}"""), Ct);

        response.IsError.Should().BeTrue();
        response.ErrorMessage.Should().Contain("timed out after 1 s");
    }

    [Test]
    public async Task CallToolAsync_CallerCancels_ReturnsErrorQuicklyAndKeepsServerRunning()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));
        var manager = scope.Resolve<ILocalServerManager>();
        await manager.ListServersAsync(Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var response = await manager.CallToolAsync(Request("test", "slow", """{"milliseconds":60000}"""), cancel.Token);
        var elapsed = started.Elapsed;
        var after = await manager.CallToolAsync(Request("test", "echo", """{"text":"still"}"""), Ct);

        response.IsError.Should().BeTrue();
        response.ErrorMessage.Should().Be("Tool call was cancelled.");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        TextOf(after).Should().Be("echo: still");
        (await CancellationsSeenByServerAsync(manager)).Should().Be(1);
    }

    /// <summary>The test server counts slow calls whose token was cancelled; it gets there after the cancellation notification arrives.</summary>
    private static async Task<int> CancellationsSeenByServerAsync(ILocalServerManager manager)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            var count = int.Parse(TextOf(await manager.CallToolAsync(Request("test", "cancellations"), timeout.Token)), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0)
            {
                return count;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    private static LocalServerConfig FilteredTestServer(string[] include, string[] exclude) => TestServer() with { IncludeTools = include, ExcludeTools = exclude };

    [Test]
    public async Task ListServersAsync_IncludeTools_ListsExactlyThose()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = FilteredTestServer(["echo", "add"], []) }));

        var catalogs = await scope.Resolve<ILocalServerManager>().ListServersAsync(Ct);

        catalogs.Single().Tools.Select(t => t.Name).Should().BeEquivalentTo("echo", "add");
    }

    [Test]
    public async Task ListServersAsync_ExcludeTools_ReportsHiddenTools()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = FilteredTestServer([], ["fail", "crash", "cancel*"]) }));
        var manager = scope.Resolve<ILocalServerManager>();

        var catalogs = await manager.ListServersAsync(Ct);

        catalogs.Single().Tools.Select(t => t.Name).Should().NotContain(["fail", "crash", "cancellations"]);
        manager.HiddenTools["test"].Should().BeEquivalentTo("fail", "crash", "cancellations");
    }

    [Test]
    public async Task CallToolAsync_HiddenTool_IsRefusedAndNeverReachesTheServer()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = FilteredTestServer(["echo", "cancellations"], []) }));
        var manager = scope.Resolve<ILocalServerManager>();
        await manager.ListServersAsync(Ct);

        var response = await manager.CallToolAsync(Request("test", "crash"), Ct);
        var after = await manager.CallToolAsync(Request("test", "echo", """{"text":"alive"}"""), Ct);

        response.IsError.Should().BeTrue();
        response.ErrorMessage.Should().Be("Tool 'crash' is not exposed by this bridge.");
        TextOf(after).Should().Be("echo: alive");
    }

    [Test]
    public async Task CallToolAsync_HiddenToolBeforeAnyListing_IsRefusedWithoutStartingTheServer()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = FilteredTestServer([], ["fail"]) }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "fail"), Ct);

        response.ErrorMessage.Should().Be("Tool 'fail' is not exposed by this bridge.");
    }

    [Test]
    public async Task ListServersAsync_OneServerCannotStart_ReturnsTheOthers()
    {
        var broken = new LocalServerConfig("this-command-does-not-exist-mcpal", [], new Dictionary<string, string>(), null, new Dictionary<string, string>());
        var scope = GetServices(ConfigWith(new() { ["broken"] = broken, ["test"] = TestServer() }));

        var catalogs = await scope.Resolve<ILocalServerManager>().ListServersAsync(Ct);

        catalogs.Select(c => c.Name).Should().Equal("test");
    }

    [Test]
    public async Task CallToolAsync_ServerProcessDiedEarlier_StartsNewProcessOnNextCall()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));
        var manager = scope.Resolve<ILocalServerManager>();
        await manager.ListServersAsync(Ct);

        var crash = await manager.CallToolAsync(Request("test", "crash"), Ct);
        var after = await manager.CallToolAsync(Request("test", "echo", """{"text":"again"}"""), Ct);

        crash.IsError.Should().BeTrue();
        after.IsError.Should().BeFalse();
        TextOf(after).Should().Be("echo: again");
    }

    [Test]
    public async Task CallToolAsync_ConcurrentCalls_RunInParallel()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));
        var manager = scope.Resolve<ILocalServerManager>();
        await manager.ListServersAsync(Ct);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => manager.CallToolAsync(Request("test", "slow", """{"milliseconds":1000}"""), Ct)));

        responses.Should().OnlyContain(r => !r.IsError);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(3000));
    }

    private static readonly UserContext Anna = new("jwt.value", "user-anna", "anna@acme.example", "Anna", ["Everyone", "hr"], Guid.Parse("7f3c6a1e-5b7d-4f0e-9a51-0c2d2f6f7a10"), "acme");

    [Test]
    public async Task CallToolAsync_WithUser_PassesTheCallerInMetaToTheLocalServer()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "whoami") with { User = Anna }, Ct);

        using var meta = JsonDocument.Parse(TextOf(response));
        meta.RootElement.GetProperty("sub").GetString().Should().Be("user-anna");
        meta.RootElement.GetProperty("token").GetString().Should().Be("jwt.value");
        meta.RootElement.GetProperty("email").GetString().Should().Be("anna@acme.example");
        meta.RootElement.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).Should().Equal("Everyone", "hr");
        meta.RootElement.GetProperty("company").GetString().Should().Be("acme");
    }

    [Test]
    public async Task CallToolAsync_WithoutUser_SendsNoCaller()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "whoami"), Ct);

        TextOf(response).Should().Be("none");
    }

    [Test]
    public async Task CallToolAsync_ServerWithUserContextOff_SendsNoCallerEvenWhenTheRequestHasOne()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() with { UserContext = false } }));

        var response = await scope.Resolve<ILocalServerManager>().CallToolAsync(Request("test", "whoami") with { User = Anna }, Ct);

        TextOf(response).Should().Be("none");
    }

    [Test]
    public async Task CallToolAsync_TwoUsersInParallel_EachCallSeesItsOwnCaller()
    {
        var scope = GetServices(ConfigWith(new() { ["test"] = TestServer() }));
        var manager = scope.Resolve<ILocalServerManager>();
        var ben = Anna with { Token = "jwt.ben", UserId = "user-ben", Email = "ben@acme.example" };

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => manager.CallToolAsync(Request("test", "whoami") with { User = i % 2 == 0 ? Anna : ben }, Ct)));

        var subs = responses.Select(r => JsonDocument.Parse(TextOf(r)).RootElement.GetProperty("sub").GetString()).ToList();
        subs.Should().Equal("user-anna", "user-ben", "user-anna", "user-ben", "user-anna", "user-ben");
    }
}

