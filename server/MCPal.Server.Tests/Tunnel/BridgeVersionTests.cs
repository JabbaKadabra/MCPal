using MCPal.Server.Tunnel;

namespace MCPal.Server.Tests.Tunnel;

[TestFixture]
internal sealed class BridgeVersionTests
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
    public void IsOlder_BridgeAgainstLatest_ComparesNumericParts(string bridge, string latest, bool expected)
    {
        BridgeVersions.IsOlder(bridge, latest).Should().Be(expected);
    }
}
