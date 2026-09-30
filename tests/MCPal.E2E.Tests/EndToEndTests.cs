using System.Diagnostics;
using ModelContextProtocol.Protocol;

namespace MCPal.E2E.Tests;

/// <summary>Real SDK MCP client, cloud host, real agent, real stdio MCP server.</summary>
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
        using var agent = await stack.StartAgentAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.RawKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__echo", Ct);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var echo = await client.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "hi" }, cancellationToken: Ct);
        var add = await client.CallToolAsync("test__add", new Dictionary<string, object?> { ["a"] = 2, ["b"] = 3 }, cancellationToken: Ct);

        tools.Select(t => t.Name).Should().Contain(["test__echo", "test__add", "test__slow", "test__fail"]);
        TextOf(echo).Should().Be("echo: hi");
        TextOf(add).Should().Be("5");
    }

    [Test]
    public async Task CallTool_LocalToolThrows_ReturnsIsError()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        using var agent = await stack.StartAgentAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.RawKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__fail", Ct);

        var result = await client.CallToolAsync("test__fail", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
    }

    [Test]
    public async Task CallTool_ToolSlowerThanCloudTimeout_ReturnsTimeoutError()
    {
        await using var stack = await E2EStack.CreateAsync(Ct, new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" });
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        using var agent = await stack.StartAgentAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.RawKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__slow", Ct);

        var result = await client.CallToolAsync("test__slow", new Dictionary<string, object?> { ["milliseconds"] = 5000 }, cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Contain("timed out");
    }

    [Test]
    public async Task CallTool_ParallelCalls_RunConcurrentlyThroughTunnel()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        using var agent = await stack.StartAgentAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.RawKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__slow", Ct);
        var clock = Stopwatch.StartNew();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.CallToolAsync("test__slow", new Dictionary<string, object?> { ["milliseconds"] = 1500 }, cancellationToken: Ct).AsTask()));

        results.Should().OnlyContain(r => r.IsError != true);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4.5));
    }

    [Test]
    public async Task AgentStopsAndRestarts_ToolsDisappearThenReturn()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var first = await stack.StartAgentAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.RawKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__echo", Ct);

        await first.StopAsync(Ct);
        first.Dispose();
        var offline = await client.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "x" }, cancellationToken: Ct);
        using var second = await stack.StartAgentAsync(acme, "test", "hq-01", Ct);
        await E2EStack.WaitForToolAsync(client, "test__echo", Ct);
        var online = await client.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "back" }, cancellationToken: Ct);

        offline.IsError.Should().BeTrue();
        TextOf(offline).Should().Contain("not available");
        TextOf(online).Should().Be("echo: back");
    }

    [Test]
    public async Task TwoCompanies_EachSeesAndReachesOnlyOwnAgent()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var globex = await stack.SeedCompanyAsync("Globex", Ct);
        using var acmeAgent = await stack.StartAgentAsync(acme, "alpha", "acme-01", Ct);
        using var globexAgent = await stack.StartAgentAsync(globex, "beta", "globex-01", Ct);
        await using var acmeClient = await stack.ConnectClientAsync(acme.RawKey, Ct);
        await using var globexClient = await stack.ConnectClientAsync(globex.RawKey, Ct);
        await E2EStack.WaitForToolAsync(acmeClient, "alpha__echo", Ct);
        await E2EStack.WaitForToolAsync(globexClient, "beta__echo", Ct);

        var acmeTools = await acmeClient.ListToolsAsync(cancellationToken: Ct);
        var crossCall = await globexClient.CallToolAsync("alpha__echo", new Dictionary<string, object?> { ["text"] = "steal" }, cancellationToken: Ct);

        acmeTools.Should().OnlyContain(t => t.Name.StartsWith("alpha__", StringComparison.Ordinal));
        crossCall.IsError.Should().BeTrue();
        TextOf(crossCall).Should().Contain("not available");
    }

    [Test]
    public async Task RevokedKey_ClosesTunnelAndHidesTools()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var other = await stack.SeedCompanyAsync("Other", Ct);
        using var agent = await stack.StartAgentAsync(acme, "test", "hq-01", Ct);
        await using var client = await stack.ConnectClientAsync(acme.RawKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__echo", Ct);

        await stack.RevokeAsync(acme, Ct);

        // The revoked key can no longer call the MCP endpoint at all.
        var act = async () => await stack.ConnectClientAsync(acme.RawKey, Ct);
        await act.Should().ThrowAsync<Exception>();
        await using var otherClient = await stack.ConnectClientAsync(other.RawKey, Ct);
        (await otherClient.ListToolsAsync(cancellationToken: Ct)).Should().BeEmpty();
    }
}
