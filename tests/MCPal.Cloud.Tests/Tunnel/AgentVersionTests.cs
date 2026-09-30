using MCPal.Cloud.Tunnel;

namespace MCPal.Cloud.Tests.Tunnel;

[TestFixture]
internal sealed class AgentVersionTests
{
    [TestCase("1.0", "1.2.0", true)]
    [TestCase("1.2.0.0", "1.2.0", false)]
    [TestCase("1.2", "1.2.1", true)]
    [TestCase("1.1.0", "1.1.0", false)]
    [TestCase("2.0.0", "1.9.9", false)]
    [TestCase("1.0.0+abc123", "1.0.1", true)]
    [TestCase("1.1.0-beta.1", "1.1.0", false)]
    [TestCase("dev", "1.0.0", false)]
    [TestCase("1.0.0", "not-a-version", false)]
    [TestCase("", "1.0.0", false)]
    public void IsOlder_AgentAgainstLatest_ComparesNumericParts(string agent, string latest, bool expected)
    {
        AgentVersions.IsOlder(agent, latest).Should().Be(expected);
    }
}
