using System.Text.Json;
using MCPal.Agent.Config;
using MCPal.Agent.Status;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Agent.Tests.Status;

[TestFixture]
internal sealed class StatusCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static string WriteStatus(string tunnel, TimeSpan age)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcpal-statuscmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var status = new AgentStatus(Now - age, tunnel, "https://cloud", Now - age, [new ServerStatus("pg", "running", 5)], []);
        var path = Path.Combine(directory, "status.json");
        File.WriteAllText(path, JsonSerializer.Serialize(status, AgentStatusFile.JsonOptions));
        return path;
    }

    private static string WriteConfig(string? statusFile)
    {
        var path = Path.Combine(Path.GetTempPath(), "mcpal-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        var status = statusFile is null ? string.Empty : $"\"statusFile\": {JsonSerializer.Serialize(statusFile)},";
        File.WriteAllText(path, $"{{ {status} \"mcpServers\": {{}} }}");
        return path;
    }

    private static (int Code, string Output) Run(string configPath)
    {
        using var output = new StringWriter();
        var code = StatusCommand.Run(configPath, new Dictionary<string, string?>(), new FakeTimeProvider(Now), output);
        return (code, output.ToString());
    }

    [Test]
    public void Run_ConnectedAndFresh_PrintsStatusAndReturnsZero()
    {
        var (code, output) = Run(WriteConfig(WriteStatus("connected", TimeSpan.FromSeconds(20))));

        code.Should().Be(0);
        output.Should().Contain("connected").And.Contain("pg").And.Contain("5 tool(s)");
    }

    [Test]
    public void Run_TunnelNotConnected_ReturnsOne()
    {
        var (code, _) = Run(WriteConfig(WriteStatus("reconnecting", TimeSpan.FromSeconds(20))));

        code.Should().Be(1);
    }

    [Test]
    public void Run_StatusFileStale_ReturnsOneAndSaysSo()
    {
        var (code, output) = Run(WriteConfig(WriteStatus("connected", TimeSpan.FromMinutes(10))));

        code.Should().Be(1);
        output.Should().Contain("stale");
    }

    [Test]
    public void Run_NoStatusFileConfigured_Throws()
    {
        var act = () => Run(WriteConfig(null));

        act.Should().Throw<AgentConfigException>().WithMessage("*statusFile*");
    }

    [Test]
    public void Run_StatusFileMissing_Throws()
    {
        var act = () => Run(WriteConfig(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"), "s.json")));

        act.Should().Throw<AgentConfigException>();
    }
}
