using MCPal.Server.Tunnel;

namespace MCPal.Server.Tests.Tunnel;

[TestFixture]
internal sealed class BridgeDownloadsTests
{
    private const string Releases = "https://github.com/JabbaKadabra/MCPal/releases";
    private const string Image = "ghcr.io/jabbakadabra/mcpal-bridge";

    [Test]
    public void For_Version_BuildsOneAssetUrlPerPlatform()
    {
        var set = BridgeDownloads.For(Releases, Image, "1.2.3");

        set.Downloads.Select(d => (d.Rid, d.Os, d.FileName, d.Url)).Should().Equal(
            ("linux-x64", "linux", "mcpal-bridge-1.2.3-linux-x64.tar.gz", $"{Releases}/download/v1.2.3/mcpal-bridge-1.2.3-linux-x64.tar.gz"),
            ("linux-arm64", "linux", "mcpal-bridge-1.2.3-linux-arm64.tar.gz", $"{Releases}/download/v1.2.3/mcpal-bridge-1.2.3-linux-arm64.tar.gz"),
            ("win-x64", "windows", "mcpal-bridge-1.2.3-win-x64.zip", $"{Releases}/download/v1.2.3/mcpal-bridge-1.2.3-win-x64.zip"));
        set.ChecksumsUrl.Should().Be($"{Releases}/download/v1.2.3/sha256sums.txt");
        set.ReleasesUrl.Should().Be($"{Releases}/tag/v1.2.3");
    }

    [TestCase("v1.2.3")]
    [TestCase("  1.2.3  ")]
    public void For_VersionWithPrefixOrWhitespace_IsNormalized(string version)
    {
        BridgeDownloads.For(Releases, Image, version).ChecksumsUrl.Should().Be($"{Releases}/download/v1.2.3/sha256sums.txt");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void For_NoVersion_FallsBackToLatestReleasePage(string? version)
    {
        var set = BridgeDownloads.For(Releases, Image, version);

        set.Downloads.Should().BeEmpty();
        set.ChecksumsUrl.Should().BeNull();
        set.ReleasesUrl.Should().Be($"{Releases}/latest");
    }

    [Test]
    public void For_BaseUrlWithTrailingSlash_DoesNotDoubleTheSlash()
    {
        BridgeDownloads.For(Releases + "/", Image, "1.0.0").ReleasesUrl.Should().Be($"{Releases}/tag/v1.0.0");
    }

    [TestCase("1.2.3", "ghcr.io/jabbakadabra/mcpal-bridge:1.2.3")]
    [TestCase("v1.2.3", "ghcr.io/jabbakadabra/mcpal-bridge:1.2.3")]
    [TestCase(null, "ghcr.io/jabbakadabra/mcpal-bridge:latest")]
    [TestCase("  ", "ghcr.io/jabbakadabra/mcpal-bridge:latest")]
    public void For_Version_PinsTheImageTagOrFallsBackToLatest(string? version, string expected)
    {
        BridgeDownloads.For(Releases, Image, version).ImageReference.Should().Be(expected);
    }
}
