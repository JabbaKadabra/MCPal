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

    private const string WithVariables = """
        {
          "cloud": { "url": "${CLOUD_URL}", "apiKey": "${CLOUD_KEY}" },
          "mcpServers": {
            "db":   { "command": "${DB_BIN}", "args": ["--dsn", "${DB_DSN}"], "env": { "PGPASSWORD": "${DB_PASSWORD}" } },
            "jira": { "url": "https://${JIRA_HOST}/mcp", "headers": { "Authorization": "Bearer ${JIRA_TOKEN}" } }
          }
        }
        """;

    private static Dictionary<string, string?> AllVariables() => new()
    {
        ["CLOUD_URL"] = "https://mcpal.example.com",
        ["CLOUD_KEY"] = "mcpal_cccccccc_key",
        ["DB_BIN"] = "/usr/bin/pg-mcp",
        ["DB_DSN"] = "host=db",
        ["DB_PASSWORD"] = "pw",
        ["JIRA_HOST"] = "jira.internal",
        ["JIRA_TOKEN"] = "tok",
    };

    [Test]
    public void Parse_VariablesInEveryField_AreExpanded()
    {
        var config = AgentConfigLoader.Parse(WithVariables, AllVariables(), requireCloud: true);

        config.Cloud.Url.Should().Be("https://mcpal.example.com");
        config.Cloud.ApiKey.Should().Be("mcpal_cccccccc_key");
        config.McpServers["db"].Command.Should().Be("/usr/bin/pg-mcp");
        config.McpServers["db"].Args.Should().Equal("--dsn", "host=db");
        config.McpServers["db"].Env["PGPASSWORD"].Should().Be("pw");
        config.McpServers["jira"].Url.Should().Be("https://jira.internal/mcp");
        config.McpServers["jira"].Headers["Authorization"].Should().Be("Bearer tok");
    }

    [Test]
    public void Parse_UnsetVariableInServer_ThrowsWithServerAndVariableName()
    {
        var environment = AllVariables();
        environment.Remove("JIRA_TOKEN");

        var act = () => AgentConfigLoader.Parse(WithVariables, environment, requireCloud: true);

        act.Should().Throw<AgentConfigException>().WithMessage("Server 'jira': environment variable 'JIRA_TOKEN' is not set.");
    }

    [Test]
    public void Parse_UnsetVariableInCloudKey_Throws()
    {
        var environment = AllVariables();
        environment.Remove("CLOUD_KEY");

        var act = () => AgentConfigLoader.Parse(WithVariables, environment, requireCloud: true);

        act.Should().Throw<AgentConfigException>().WithMessage("*'CLOUD_KEY' is not set*");
    }

    [Test]
    public void Parse_ApiKeyFromEnvironmentOverride_DoesNotNeedVariableOfConfiguredKey()
    {
        var environment = AllVariables();
        environment.Remove("CLOUD_KEY");
        environment["MCPAL_API_KEY"] = "mcpal_dddddddd_override";

        var config = AgentConfigLoader.Parse(WithVariables, environment, requireCloud: true);

        config.Cloud.ApiKey.Should().Be("mcpal_dddddddd_override");
    }

    [Test]
    public void Parse_CloudNotRequired_DoesNotFailOnUnsetCloudVariables()
    {
        var environment = AllVariables();
        environment.Remove("CLOUD_URL");
        environment.Remove("CLOUD_KEY");

        var config = AgentConfigLoader.Parse(WithVariables, environment, requireCloud: false);

        config.McpServers.Should().ContainKey("db");
    }

    [Test]
    public void Parse_DefaultInConfig_IsUsedWhenVariableUnset()
    {
        const string json = """{ "mcpServers": { "a": { "command": "run", "args": ["${MODE:-fast}"] } } }""";

        var config = AgentConfigLoader.Parse(json, NoEnvironment, requireCloud: false);

        config.McpServers["a"].Args.Should().Equal("fast");
    }

    [Test]
    public void Parse_IncludeAndExcludeTools_AreRead()
    {
        const string json = """{ "mcpServers": { "pg": { "command": "x", "includeTools": ["query", "list_*"], "excludeTools": ["drop_*"] } } }""";

        var server = AgentConfigLoader.Parse(json, NoEnvironment, requireCloud: false).McpServers["pg"];

        server.IncludeTools.Should().Equal("query", "list_*");
        server.ExcludeTools.Should().Equal("drop_*");
    }

    [Test]
    public void Parse_NoToolLists_GivesEmptyLists()
    {
        const string json = """{ "mcpServers": { "pg": { "command": "x" } } }""";

        var server = AgentConfigLoader.Parse(json, NoEnvironment, requireCloud: false).McpServers["pg"];

        server.IncludeTools.Should().BeEmpty();
        server.ExcludeTools.Should().BeEmpty();
    }

    [TestCase("""{ "mcpServers": { "pg": { "command": "x", "includeTools": [""] } } }""")]
    [TestCase("""{ "mcpServers": { "pg": { "command": "x", "excludeTools": ["  "] } } }""")]
    public void Parse_EmptyPattern_ThrowsNamingTheServer(string json)
    {
        var act = () => AgentConfigLoader.Parse(json, NoEnvironment, requireCloud: false);

        act.Should().Throw<AgentConfigException>().WithMessage("Server 'pg'*");
    }

    [Test]
    public void Parse_StatusFile_IsReadAndExpanded()
    {
        var environment = new Dictionary<string, string?> { ["STATE_DIR"] = "/var/lib/mcpal" };
        const string json = """{ "statusFile": "${STATE_DIR}/status.json", "mcpServers": {} }""";

        var config = AgentConfigLoader.Parse(json, environment, requireCloud: false);

        config.StatusFile.Should().Be("/var/lib/mcpal/status.json");
    }

    [Test]
    public void Parse_NoStatusFile_IsNull()
    {
        AgentConfigLoader.Parse("""{ "mcpServers": {} }""", NoEnvironment, requireCloud: false).StatusFile.Should().BeNull();
    }
}
