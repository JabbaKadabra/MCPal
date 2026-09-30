using MCPal.Agent.Config;

namespace MCPal.Agent.Tests.Config;

[TestFixture]
internal sealed class AgentConfigLoaderTests
{
    private const string Full = """
        {
          // comments are allowed
          "cloud": { "url": "https://mcpal.example.com/", "apiKey": "mcpal_aaaaaaaa_key", "agentName": "hq-01" },
          "mcpServers": {
            "kb":   { "command": "npx", "args": ["-y", "@acme/kb-mcp"], "env": { "KB_TOKEN": "secret" } },
            "wiki": { "url": "http://intranet:8080/mcp", "headers": { "Authorization": "Bearer x" } }
          }
        }
        """;

    private static readonly IReadOnlyDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    [Test]
    public void Parse_FullConfig_ReadsCloudAndBothServerKinds()
    {
        var config = AgentConfigLoader.Parse(Full, NoEnvironment, requireCloud: true);

        config.Cloud.Url.Should().Be("https://mcpal.example.com");
        config.Cloud.ApiKey.Should().Be("mcpal_aaaaaaaa_key");
        config.Cloud.AgentName.Should().Be("hq-01");
        config.McpServers["kb"].Command.Should().Be("npx");
        config.McpServers["kb"].Args.Should().Equal("-y", "@acme/kb-mcp");
        config.McpServers["kb"].Env["KB_TOKEN"].Should().Be("secret");
        config.McpServers["wiki"].Url.Should().Be("http://intranet:8080/mcp");
        config.McpServers["wiki"].Headers["Authorization"].Should().Be("Bearer x");
    }

    [Test]
    public void Parse_EnvironmentApiKey_OverridesConfiguredKey()
    {
        var environment = new Dictionary<string, string?> { ["MCPAL_API_KEY"] = "mcpal_bbbbbbbb_fromenv" };

        var config = AgentConfigLoader.Parse(Full, environment, requireCloud: true);

        config.Cloud.ApiKey.Should().Be("mcpal_bbbbbbbb_fromenv");
    }

    [Test]
    public void Parse_MissingAgentName_UsesMachineName()
    {
        const string json = """{ "cloud": { "url": "https://x.example", "apiKey": "mcpal_a_b" }, "mcpServers": {} }""";

        var config = AgentConfigLoader.Parse(json, NoEnvironment, requireCloud: true);

        config.Cloud.AgentName.Should().Be(Environment.MachineName);
    }

    [Test]
    public void Parse_ServerWithCommandAndUrl_Throws()
    {
        const string json = """{ "mcpServers": { "bad": { "command": "x", "url": "http://y" } } }""";

        var act = () => AgentConfigLoader.Parse(json, NoEnvironment, requireCloud: false);

        act.Should().Throw<AgentConfigException>().WithMessage("*bad*command*url*");
    }

    [Test]
    public void Parse_ServerWithoutCommandOrUrl_Throws()
    {
        const string json = """{ "mcpServers": { "empty": { } } }""";

        var act = () => AgentConfigLoader.Parse(json, NoEnvironment, requireCloud: false);

        act.Should().Throw<AgentConfigException>().WithMessage("*empty*");
    }

    [Test]
    public void Parse_RelativeServerUrl_Throws()
    {
        const string json = """{ "mcpServers": { "wiki": { "url": "/mcp" } } }""";

        var act = () => AgentConfigLoader.Parse(json, NoEnvironment, requireCloud: false);

        act.Should().Throw<AgentConfigException>().WithMessage("*wiki*url*");
    }

    [TestCase("""{ "mcpServers": {} }""")]
    [TestCase("""{ "cloud": { "url": "https://x.example" }, "mcpServers": {} }""")]
    [TestCase("""{ "cloud": { "url": "not a url", "apiKey": "k" }, "mcpServers": {} }""")]
    public void Parse_IncompleteCloudSection_ThrowsWhenCloudRequired(string json)
    {
        var act = () => AgentConfigLoader.Parse(json, NoEnvironment, requireCloud: true);

        act.Should().Throw<AgentConfigException>().WithMessage("*cloud*");
    }

    [Test]
    public void Parse_NoCloudSection_IsAllowedWhenCloudNotRequired()
    {
        var config = AgentConfigLoader.Parse("""{ "mcpServers": { "a": { "command": "x" } } }""", NoEnvironment, requireCloud: false);

        config.McpServers.Should().ContainKey("a");
    }

    [Test]
    public void Parse_InvalidJson_ThrowsConfigException()
    {
        var act = () => AgentConfigLoader.Parse("{ nope", NoEnvironment, requireCloud: false);

        act.Should().Throw<AgentConfigException>().WithMessage("*JSON*");
    }

    [Test]
    public void Load_MissingFile_ThrowsConfigExceptionWithPath()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "mcpal.json");

        var act = () => AgentConfigLoader.Load(path, NoEnvironment, requireCloud: false);

        act.Should().Throw<AgentConfigException>().WithMessage($"*{path}*");
    }
}
