using System.Diagnostics;
using MCPal.Server.Tunnel;
using MCPal.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;

namespace MCPal.E2E.Tests;

/// <summary>Real SDK MCP client, server host, real bridge, real stdio MCP server.</summary>
[TestFixture]
internal sealed class EndToEndTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static string TextOf(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    [Test]
    public async Task ListAndCall_ThroughWholeStack_ReturnsLocalServerResults()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__echo", Ct);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var echo = await client.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "hi" }, cancellationToken: Ct);
        var add = await client.CallToolAsync("test__add", new Dictionary<string, object?> { ["a"] = 2, ["b"] = 3 }, cancellationToken: Ct);

        tools.Select(t => t.Name).Should().Contain(["test__echo", "test__add", "test__slow", "test__fail"]);
        TextOf(echo).Should().Be("echo: hi");
        TextOf(add).Should().Be("5");
    }

    [Test]
    public async Task ListAndCall_ToolWithOutputSchema_SeesSameSchemaAndStructuredContentAsLocalServer()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__weather", Ct);
        await using var local = await E2EStack.ConnectLocalServerAsync(Ct);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var localTools = await local.ListToolsAsync(cancellationToken: Ct);
        var viaServer = await client.CallToolAsync("test__weather", new Dictionary<string, object?> { ["city"] = "Linz" }, cancellationToken: Ct);
        var direct = await local.CallToolAsync("weather", new Dictionary<string, object?> { ["city"] = "Linz" }, cancellationToken: Ct);

        var serverSchema = tools.Single(t => t.Name == "test__weather").ProtocolTool.OutputSchema;
        var localSchema = localTools.Single(t => t.Name == "weather").ProtocolTool.OutputSchema;
        serverSchema.Should().NotBeNull();
        serverSchema?.GetRawText().Should().Be(localSchema?.GetRawText());
        viaServer.StructuredContent?.GetRawText().Should().Be(direct.StructuredContent?.GetRawText());
        viaServer.StructuredContent?.GetProperty("city").GetString().Should().Be("Linz");
    }

    [Test]
    public async Task CallTool_LocalToolThrows_ReturnsIsError()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__fail", Ct);

        var result = await client.CallToolAsync("test__fail", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
    }

    [Test]
    public async Task CallTool_ToolSlowerThanServerTimeout_ReturnsTimeoutError()
    {
        await using var stack = await E2EStack.CreateAsync(Ct, new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" });
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__slow", Ct);

        var result = await client.CallToolAsync("test__slow", new Dictionary<string, object?> { ["milliseconds"] = 5000 }, cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Contain("timed out");
    }

    [Test]
    public async Task CallTool_ServerTimesOut_LocalToolIsCancelledLongBeforeItsOwnEnd()
    {
        await using var stack = await E2EStack.CreateAsync(Ct, new() { ["Mcpal:ToolCallTimeoutSeconds"] = "2" });
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__slow", Ct);
        var clock = Stopwatch.StartNew();

        var result = await client.CallToolAsync("test__slow", new Dictionary<string, object?> { ["milliseconds"] = 30000 }, cancellationToken: Ct);
        var cancellations = await WaitForCancellationsAsync(client);

        TextOf(result).Should().Contain("timed out after 2 s");
        cancellations.Should().Be(1);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    /// <summary>Polls the test server until it saw a cancelled call.</summary>
    private static async Task<int> WaitForCancellationsAsync(ModelContextProtocol.Client.McpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var count = int.Parse(TextOf(await client.CallToolAsync("test__cancellations", cancellationToken: timeout.Token)), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0)
            {
                return count;
            }

            await Task.Delay(100, timeout.Token);
        }
    }

    [Test]
    public async Task StatusFile_BridgeConnectsAndStops_ShowsServersThenDisconnected()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var statusFile = Path.Combine(Path.GetTempPath(), "mcpal-e2e-" + Guid.NewGuid().ToString("N"), "status.json");
        var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct, statusFile: statusFile);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__echo", Ct);

        var running = await WaitForStatusAsync(statusFile, status => status.LastRegisteredAt is not null);
        await bridge.StopAsync(Ct);
        bridge.Dispose();
        var stopped = MCPal.Bridge.Status.BridgeStatusFile.Read(statusFile);

        running.Tunnel.Should().Be("connected");
        running.Servers.Should().ContainSingle().Which.State.Should().Be("running");
        running.Servers[0].Tools.Should().BeGreaterThanOrEqualTo(4);
        stopped.Tunnel.Should().Be("disconnected");
    }

    private static async Task<MCPal.Bridge.Status.BridgeStatus> WaitForStatusAsync(string path, Func<MCPal.Bridge.Status.BridgeStatus, bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (File.Exists(path) && MCPal.Bridge.Status.BridgeStatusFile.Read(path) is { } status && condition(status))
            {
                return status;
            }

            await Task.Delay(100, timeout.Token);
        }
    }

    [Test]
    public async Task Bridge_ProtocolRejectedByServer_LogsClearMessageAndBacksOffLongBeforeTryingAgain()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new CapturingLoggerProvider();

        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct, timeProvider: time, protocolVersion: "9.0", logs: logs);
        var first = await logs.WaitForAsync(entry => entry.Message.Contains("too old or too new", StringComparison.Ordinal), 1, Ct);
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(500, Ct);
        var afterFiveMinutes = logs.Entries.Count(entry => entry.Message.Contains("too old or too new", StringComparison.Ordinal));

        // The 15 minute wait starts when the supervise loop begins, which can be just after the first log line.
        // Advancing past it once or twice always wakes the loop; the wait below then sees the second attempt.
        for (var i = 0; i < 3 && logs.Entries.Count(entry => entry.Message.Contains("too old or too new", StringComparison.Ordinal)) < 2; i++)
        {
            time.Advance(TimeSpan.FromMinutes(16));
            await Task.Delay(300, Ct);
        }
        var second = await logs.WaitForAsync(entry => entry.Message.Contains("too old or too new", StringComparison.Ordinal), 2, Ct);

        first[0].Level.Should().Be(LogLevel.Error);
        first[0].Message.Should().Contain("protocol 9.0").And.Contain(stack.Server.BaseAddress.ToString().TrimEnd('/')).And.Contain("Download");
        afterFiveMinutes.Should().Be(1, "an incompatible bridge must not register every 30 seconds");
        second[1].Level.Should().Be(LogLevel.Debug, "repeats of the same rejection stay quiet");
    }

    [Test]
    public async Task CallTool_ParallelCalls_RunConcurrentlyThroughTunnel()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__slow", Ct);
        var clock = Stopwatch.StartNew();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.CallToolAsync("test__slow", new Dictionary<string, object?> { ["milliseconds"] = 1500 }, cancellationToken: Ct).AsTask()));

        results.Should().OnlyContain(r => r.IsError != true);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4.5));
    }

    [Test]
    public async Task BridgeStopsAndRestarts_ToolsDisappearThenReturn()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var first = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__echo", Ct);

        await first.StopAsync(Ct);
        first.Dispose();
        var offline = await client.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "x" }, cancellationToken: Ct);
        using var second = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await E2EStack.WaitForToolAsync(client, "test__echo", Ct);
        var online = await client.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "back" }, cancellationToken: Ct);

        offline.IsError.Should().BeTrue();
        TextOf(offline).Should().Contain("not available");
        TextOf(online).Should().Be("echo: back");
    }

    [Test]
    public async Task TwoCompanies_EachSeesAndReachesOnlyOwnBridge()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var globex = await stack.SeedCompanyAsync("Globex", Ct);
        using var acmeBridge = await stack.StartBridgeAsync(acme, "alpha", "acme-01", Ct);
        using var globexBridge = await stack.StartBridgeAsync(globex, "beta", "globex-01", Ct);
        await using var acmeClient = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await using var globexClient = await stack.ConnectClientAsync(globex.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(acmeClient, "alpha__echo", Ct);
        await E2EStack.WaitForToolAsync(globexClient, "beta__echo", Ct);

        var acmeTools = await acmeClient.ListToolsAsync(cancellationToken: Ct);
        var crossCall = await globexClient.CallToolAsync("alpha__echo", new Dictionary<string, object?> { ["text"] = "steal" }, cancellationToken: Ct);

        acmeTools.Should().OnlyContain(t => t.Name.StartsWith("alpha__", StringComparison.Ordinal));
        crossCall.IsError.Should().BeTrue();
        TextOf(crossCall).Should().Contain("not available");
    }

    [Test]
    public async Task ServerNameHeldByStaleConnection_BridgeRegistersItOnceFreed()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var stale = stack.CreateTunnelConnection(acme.BridgeKey);
        await stale.StartAsync(Ct);
        var catalog = new BridgeCatalog("hq-01", "1.0", ProtocolVersion.Current, [new ServerCatalog("test", [new ToolDescriptor("old", null, null, "{\"type\":\"object\"}", null)])]);
        await stale.InvokeAsync<RegisterResult>("Register", catalog, Ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct, time);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__old", Ct);
        var registry = stack.Services.GetRequiredService<ConnectionRegistry>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!registry.Connections(acme.CompanyId).Any(c => c.RejectedServers.Count > 0))
        {
            await Task.Delay(50, timeout.Token);
        }

        await stale.DisposeAsync();

        while (!(await client.ListToolsAsync(cancellationToken: timeout.Token)).Any(t => t.Name == "test__echo"))
        {
            time.Advance(TimeSpan.FromSeconds(31));
            await Task.Delay(100, timeout.Token);
        }
    }

    [Test]
    public async Task RevokedBridgeKey_ClosesTunnelAndHidesTools()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__echo", Ct);

        await stack.RevokeAsync(acme, acme.BridgeKeyId, Ct);

        // Revoking the bridge key closes the tunnel at once: the tools are gone for the still valid personal access token.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while ((await client.ListToolsAsync(cancellationToken: timeout.Token)).Count > 0)
        {
            await Task.Delay(100, timeout.Token);
        }
    }

    [Test]
    public async Task RevokedPersonalToken_CannotCallTheMcpEndpointButOtherCompaniesStillCan()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var other = await stack.SeedCompanyAsync("Other", Ct);

        await stack.RevokeAsync(acme, acme.PersonalKeyId, Ct);

        var act = async () => await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await act.Should().ThrowAsync<Exception>();
        await using var otherClient = await stack.ConnectClientAsync(other.PersonalKey, Ct);
        (await otherClient.ListToolsAsync(cancellationToken: Ct)).Should().BeEmpty();
    }
}
