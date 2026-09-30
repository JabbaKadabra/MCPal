using System.Text.Json;
using Autofac;
using MCPal.Agent.Config;
using MCPal.Agent.Local;
using MCPal.Agent.Tests.Infrastructure;
using MCPal.Contracts;

namespace MCPal.Agent.Tests.Local;

[TestFixture]
internal sealed class LocalServerManagerTests : AgentTestBase
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
}
