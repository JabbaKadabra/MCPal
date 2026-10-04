using MCPal.Bridge.Config;
using MCPal.Bridge.Enrollment;

namespace MCPal.Bridge.Tests.Config;

[TestFixture]
internal sealed class BridgeConfigCredentialsTests
{
    private const string UrlOnly = """{ "mcpal": { "url": "https://mcpal.example.com" } }""";

    private static readonly IReadOnlyDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcpal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteConfig(string directory, string json)
    {
        var path = Path.Combine(directory, "mcpal.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Test]
    public void Load_NoKeyInConfigOrEnvironment_UsesTheCredentialsFileNextToTheConfig()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, UrlOnly);
            BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");

            BridgeConfigLoader.Load(config, NoEnvironment, requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_aaaaaaaa_stored");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_EnvironmentAndStoredKey_EnvironmentWins()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, UrlOnly);
            BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");
            var environment = new Dictionary<string, string?> { ["MCPAL_API_KEY"] = "mcpal_bbbbbbbb_env" };

            BridgeConfigLoader.Load(config, environment, requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_bbbbbbbb_env");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_EmptyEnvironmentKeyAndStoredKey_UsesTheStoredKey()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, UrlOnly);
            BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");
            var environment = new Dictionary<string, string?> { ["MCPAL_API_KEY"] = string.Empty };

            BridgeConfigLoader.Load(config, environment, requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_aaaaaaaa_stored");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_ConfigKeyAndStoredKey_ConfigWins()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, """{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "mcpal_cccccccc_config" } }""");
            BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");

            BridgeConfigLoader.Load(config, NoEnvironment, requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_cccccccc_config");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_CredentialsFileOption_IsRelativeToTheConfig()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, """{ "mcpal": { "url": "https://mcpal.example.com", "credentialsFile": "state/keys.json" } }""");
            BridgeCredentials.Write(Path.Combine(directory, "state", "keys.json"), "mcpal_aaaaaaaa_elsewhere");

            BridgeConfigLoader.Load(config, NoEnvironment, requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_aaaaaaaa_elsewhere");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_NoKeyAnywhere_MessageNamesTheEnrollmentVariable()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, UrlOnly);

            var act = () => BridgeConfigLoader.Load(config, NoEnvironment, requireMcpal: true);

            act.Should().Throw<BridgeConfigException>().WithMessage("*MCPAL_API_KEY*MCPAL_ENROLL*");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LoadEnrollmentTarget_UrlWithVariable_IsExpanded()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, """{ "mcpal": { "url": "${MCPAL_URL}", "bridgeName": "hq-01" } }""");
            var environment = new Dictionary<string, string?> { ["MCPAL_URL"] = "https://mcpal.example.com" };

            var target = BridgeConfigLoader.LoadEnrollmentTarget(config, environment, urlOverride: null);

            target.Url.Should().Be("https://mcpal.example.com");
            target.BridgeName.Should().Be("hq-01");
            target.CredentialsPath.Should().Be(Path.Combine(directory, "credentials.json"));
            target.HasKey.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LoadEnrollmentTarget_UrlOverride_SkipsExpansionOfTheConfiguredUrl()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, """{ "mcpal": { "url": "${MCPAL_URL_NOT_SET}" } }""");

            var target = BridgeConfigLoader.LoadEnrollmentTarget(config, NoEnvironment, urlOverride: "https://given.example.com");

            target.Url.Should().Be("https://given.example.com");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LoadEnrollmentTarget_NoUrlAnywhere_ReturnsNullUrl()
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, "{}");

            BridgeConfigLoader.LoadEnrollmentTarget(config, NoEnvironment, urlOverride: null).Url.Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase("env")]
    [TestCase("config")]
    [TestCase("file")]
    public void LoadEnrollmentTarget_KeyFromAnySource_HasKey(string source)
    {
        var directory = NewDirectory();
        try
        {
            var config = WriteConfig(directory, source == "config"
                ? """{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "mcpal_cccccccc_config" } }"""
                : UrlOnly);
            if (source == "file")
            {
                BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");
            }

            var environment = new Dictionary<string, string?>();
            if (source == "env")
            {
                environment["MCPAL_API_KEY"] = "mcpal_bbbbbbbb_env";
            }

            BridgeConfigLoader.LoadEnrollmentTarget(config, environment, urlOverride: null).HasKey.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
