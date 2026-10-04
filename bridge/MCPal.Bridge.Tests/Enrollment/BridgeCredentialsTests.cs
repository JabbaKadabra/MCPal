using System.Runtime.Versioning;
using MCPal.Bridge.Config;
using MCPal.Bridge.Enrollment;

namespace MCPal.Bridge.Tests.Enrollment;

[TestFixture]
internal sealed class BridgeCredentialsTests
{
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcpal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public void WriteThenTryRead_RoundTripsTheKey()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "credentials.json");

            BridgeCredentials.Write(path, "mcpal_aaaaaaaa_secret");

            BridgeCredentials.TryRead(path).Should().Be("mcpal_aaaaaaaa_secret");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Write_MissingDirectory_CreatesIt()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "sub", "credentials.json");

            BridgeCredentials.Write(path, "mcpal_aaaaaaaa_secret");

            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Platform("Linux,MacOsX")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void Write_NewFile_HasModeSixHundred()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "credentials.json");

            BridgeCredentials.Write(path, "mcpal_aaaaaaaa_secret");

            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Platform("Linux,MacOsX")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void Write_ExistingFileWithWiderMode_NarrowsItToSixHundred()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "credentials.json");
            File.WriteAllText(path, "{}");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            BridgeCredentials.Write(path, "mcpal_aaaaaaaa_secret");

            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void TryRead_MissingFile_ReturnsNull()
    {
        BridgeCredentials.TryRead(Path.Combine(Path.GetTempPath(), "mcpal-missing-" + Guid.NewGuid().ToString("N"), "credentials.json")).Should().BeNull();
    }

    [Test]
    public void TryRead_FileWithoutKey_ReturnsNull()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "credentials.json");
            File.WriteAllText(path, "{ \"apiKey\": \"  \" }");

            BridgeCredentials.TryRead(path).Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void TryRead_InvalidJson_ThrowsConfigExceptionNamingThePath()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "credentials.json");
            File.WriteAllText(path, "not json");

            var act = () => BridgeCredentials.TryRead(path);

            act.Should().Throw<BridgeConfigException>().WithMessage($"*{path}*");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
