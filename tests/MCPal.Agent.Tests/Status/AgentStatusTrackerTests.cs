using System.Text.Json;
using Autofac;
using MCPal.Agent.Config;
using MCPal.Agent.Status;
using MCPal.Agent.Tests.Infrastructure;
using MCPal.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Agent.Tests.Status;

[TestFixture]
internal sealed class AgentStatusTrackerTests : AgentTestBase
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static string NewStatusPath() => Path.Combine(Path.GetTempPath(), "mcpal-status-" + Guid.NewGuid().ToString("N"), "status.json");

    private static ServerCatalog Catalog(string name, int tools) =>
        new(name, [.. Enumerable.Range(0, tools).Select(i => new ToolDescriptor($"t{i}", null, null, "{\"type\":\"object\"}", null))]);

    private (AgentStatusTracker Tracker, FakeTimeProvider Time, string Path) Arrange(params string[] configuredServers)
    {
        var path = NewStatusPath();
        var servers = configuredServers.ToDictionary(name => name, _ => TestServer());
        var config = ConfigWith(servers) with { StatusFile = path };
        var time = new FakeTimeProvider(Start);
        var scope = GetServices(config, builder => builder.RegisterInstance<TimeProvider>(time));
        return (scope.Resolve<AgentStatusTracker>(), time, path);
    }

    private static AgentStatus ReadStatus(string path) => AgentStatusFile.Read(path);

    [Test]
    public async Task SetTunnelAsync_Connected_WritesStateWithFakeTime()
    {
        var (tracker, _, path) = Arrange("pg");

        await tracker.SetTunnelAsync(TunnelState.Connected, Ct);

        var status = ReadStatus(path);
        status.Tunnel.Should().Be("connected");
        status.CloudUrl.Should().Be("http://localhost");
        status.UpdatedAt.Should().Be(Start);
    }

    [Test]
    public async Task RegisteredAsync_AcceptedServers_ListsRunningServersWithToolCounts()
    {
        var (tracker, time, path) = Arrange("pg", "wiki");
        await tracker.SetTunnelAsync(TunnelState.Connected, Ct);
        time.Advance(TimeSpan.FromMinutes(1));

        await tracker.RegisteredAsync([Catalog("pg", 5), Catalog("wiki", 2)], new RegisterResult(true, [], [], null), Ct);

        var status = ReadStatus(path);
        status.LastRegisteredAt.Should().Be(Start.AddMinutes(1));
        status.UpdatedAt.Should().Be(Start.AddMinutes(1));
        status.Servers.Should().BeEquivalentTo([new ServerStatus("pg", "running", 5), new ServerStatus("wiki", "running", 2)]);
        status.Rejected.Should().BeEmpty();
    }

    [Test]
    public async Task RegisteredAsync_ServerRejectedByCloud_IsListedAsRejectedWithReason()
    {
        var (tracker, _, path) = Arrange("pg", "wiki");
        var result = new RegisterResult(true, [new RejectedServer("wiki", "name in use")], [new RejectedTool("pg", "t1", "bad schema")], null);

        await tracker.RegisteredAsync([Catalog("pg", 2), Catalog("wiki", 1)], result, Ct);

        var status = ReadStatus(path);
        status.Servers.Should().BeEquivalentTo([new ServerStatus("pg", "running", 1), new ServerStatus("wiki", "rejected", 0)]);
        status.Rejected.Should().BeEquivalentTo([new RejectedStatus("wiki", "name in use"), new RejectedStatus("pg", "tool 't1': bad schema")]);
    }

    [Test]
    public async Task RegisteredAsync_ConfiguredServerDidNotStart_IsListedAsFailed()
    {
        var (tracker, _, path) = Arrange("pg", "broken");

        await tracker.RegisteredAsync([Catalog("pg", 1)], new RegisterResult(true, [], [], null), Ct);

        ReadStatus(path).Servers.Should().Contain(new ServerStatus("broken", "failed", 0));
    }

    [Test]
    public async Task RegisteredAsync_AgentRejectedByCloud_IsListedWithMessage()
    {
        var (tracker, _, path) = Arrange("pg");

        await tracker.RegisteredAsync([Catalog("pg", 1)], new RegisterResult(false, [], [], "Unsupported protocol version '9.0'."), Ct);

        ReadStatus(path).Rejected.Should().ContainSingle().Which.Reason.Should().Contain("Unsupported protocol");
    }

    [Test]
    public async Task SetTunnelAsync_Disconnected_KeepsLastKnownServers()
    {
        var (tracker, time, path) = Arrange("pg");
        await tracker.RegisteredAsync([Catalog("pg", 3)], new RegisterResult(true, [], [], null), Ct);
        time.Advance(TimeSpan.FromMinutes(5));

        await tracker.SetTunnelAsync(TunnelState.Disconnected, Ct);

        var status = ReadStatus(path);
        status.Tunnel.Should().Be("disconnected");
        status.UpdatedAt.Should().Be(Start.AddMinutes(5));
        status.Servers.Should().ContainSingle().Which.Tools.Should().Be(3);
    }

    [Test]
    public async Task SetTunnelAsync_ManyWrites_NeverLeaveTempFilesBehind()
    {
        var (tracker, _, path) = Arrange("pg");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => tracker.SetTunnelAsync(TunnelState.Reconnecting, Ct)));

        Directory.GetFiles(Path.GetDirectoryName(path) ?? string.Empty).Should().Equal(path);
        ReadStatus(path).Tunnel.Should().Be("reconnecting");
    }

    [Test]
    public async Task SetTunnelAsync_NoStatusFileConfigured_WritesNothingAndDoesNotThrow()
    {
        var scope = GetServices(ConfigWith(new() { ["pg"] = TestServer() }));

        await scope.Resolve<AgentStatusTracker>().SetTunnelAsync(TunnelState.Connected, Ct);
    }

    [Test]
    public async Task SetTunnelAsync_StatusFileNotWritable_DoesNotThrow()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "mcpal-status-file-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "x", Ct);
        var config = ConfigWith(new() { ["pg"] = TestServer() }) with { StatusFile = Path.Combine(blocker, "status.json") };
        var scope = GetServices(config);

        var act = async () => await scope.Resolve<AgentStatusTracker>().SetTunnelAsync(TunnelState.Connected, Ct);

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task Read_WrittenFile_UsesCamelCaseJson()
    {
        var (tracker, _, path) = Arrange("pg");
        await tracker.SetTunnelAsync(TunnelState.Connected, Ct);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct));

        document.RootElement.TryGetProperty("updatedAt", out _).Should().BeTrue();
        document.RootElement.TryGetProperty("lastRegisteredAt", out _).Should().BeTrue();
        document.RootElement.GetProperty("servers").ValueKind.Should().Be(JsonValueKind.Array);
    }
}
