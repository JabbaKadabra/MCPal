using MCPal.Bridge.Config;

namespace MCPal.Bridge.Tests.Config;

[TestFixture]
internal sealed class BridgeConfigLoaderTests
{
    private const string Full = """
        {
          // comments are allowed
          "mcpal": { "url": "https://mcpal.example.com/", "apiKey": "mcpal_aaaaaaaa_key", "bridgeName": "hq-01" },
          "mcpServers": {
            "kb":   { "command": "npx", "args": ["-y", "@acme/kb-mcp"], "env": { "KB_TOKEN": "secret" } },
            "wiki": { "url": "http://intranet:8080/mcp", "headers": { "Authorization": "Bearer x" } }
          }
        }
        """;

    private static readonly IReadOnlyDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    [Test]
    public void Parse_FullConfig_ReadsMcpalAndBothServerKinds()
    {
        var config = BridgeConfigLoader.Parse(Full, NoEnvironment, requireMcpal: true);

        config.Mcpal.Url.Should().Be("https://mcpal.example.com");
        config.Mcpal.ApiKey.Should().Be("mcpal_aaaaaaaa_key");
        config.Mcpal.BridgeName.Should().Be("hq-01");
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

        var config = BridgeConfigLoader.Parse(Full, environment, requireMcpal: true);

        config.Mcpal.ApiKey.Should().Be("mcpal_bbbbbbbb_fromenv");
    }

    [Test]
    public void Parse_MissingBridgeName_UsesMachineName()
    {
        const string json = """{ "mcpal": { "url": "https://x.example", "apiKey": "mcpal_a_b" }, "mcpServers": {} }""";

        var config = BridgeConfigLoader.Parse(json, NoEnvironment, requireMcpal: true);

        config.Mcpal.BridgeName.Should().Be(Environment.MachineName);
    }

    [Test]
    public void Parse_ServerWithCommandAndUrl_Throws()
    {
        const string json = """{ "mcpServers": { "bad": { "command": "x", "url": "http://y" } } }""";

        var act = () => BridgeConfigLoader.Parse(json, NoEnvironment, requireMcpal: false);

        act.Should().Throw<BridgeConfigException>().WithMessage("*bad*command*url*");
    }

    [Test]
    public void Parse_ServerWithoutCommandOrUrl_Throws()
    {
        const string json = """{ "mcpServers": { "empty": { } } }""";

        var act = () => BridgeConfigLoader.Parse(json, NoEnvironment, requireMcpal: false);

        act.Should().Throw<BridgeConfigException>().WithMessage("*empty*");
    }

    [Test]
    public void Parse_RelativeServerUrl_Throws()
    {
        const string json = """{ "mcpServers": { "wiki": { "url": "/mcp" } } }""";

        var act = () => BridgeConfigLoader.Parse(json, NoEnvironment, requireMcpal: false);

        act.Should().Throw<BridgeConfigException>().WithMessage("*wiki*url*");
    }

    [TestCase("""{ "mcpServers": {} }""")]
    [TestCase("""{ "mcpal": { "url": "https://x.example" }, "mcpServers": {} }""")]
    [TestCase("""{ "mcpal": { "url": "not a url", "apiKey": "k" }, "mcpServers": {} }""")]
    public void Parse_IncompleteMcpalSection_ThrowsWhenMcpalRequired(string json)
    {
        var act = () => BridgeConfigLoader.Parse(json, NoEnvironment, requireMcpal: true);

        act.Should().Throw<BridgeConfigException>().WithMessage("*mcpal*");
    }

    [Test]
    public void Parse_NoMcpalSection_IsAllowedWhenMcpalNotRequired()
    {
        var config = BridgeConfigLoader.Parse("""{ "mcpServers": { "a": { "command": "x" } } }""", NoEnvironment, requireMcpal: false);

        config.McpServers.Should().ContainKey("a");
    }

    [Test]
    public void Parse_InvalidJson_ThrowsConfigException()
    {
        var act = () => BridgeConfigLoader.Parse("{ nope", NoEnvironment, requireMcpal: false);

        act.Should().Throw<BridgeConfigException>().WithMessage("*JSON*");
    }

    [Test]
    public void Load_MissingFile_ThrowsConfigExceptionWithPath()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "mcpal.json");

        var act = () => BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: false);

        act.Should().Throw<BridgeConfigException>().WithMessage($"*{path}*");
    }

    private const string WithVariables = """
        {
          "mcpal": { "url": "${MCPAL_TEST_URL}", "apiKey": "${MCPAL_TEST_KEY}" },
          "mcpServers": {
            "db":   { "command": "${DB_BIN}", "args": ["--dsn", "${DB_DSN}"], "env": { "PGPASSWORD": "${DB_PASSWORD}" } },
            "jira": { "url": "https://${JIRA_HOST}/mcp", "headers": { "Authorization": "Bearer ${JIRA_TOKEN}" } }
          }
        }
        """;

    private static Dictionary<string, string?> AllVariables() => new()
    {
        ["MCPAL_TEST_URL"] = "https://mcpal.example.com",
        ["MCPAL_TEST_KEY"] = "mcpal_cccccccc_key",
        ["DB_BIN"] = "/usr/bin/pg-mcp",
        ["DB_DSN"] = "host=db",
        ["DB_PASSWORD"] = "pw",
        ["JIRA_HOST"] = "jira.internal",
        ["JIRA_TOKEN"] = "tok",
    };

    [Test]
    public void Parse_VariablesInEveryField_AreExpanded()
    {
        var config = BridgeConfigLoader.Parse(WithVariables, AllVariables(), requireMcpal: true);

        config.Mcpal.Url.Should().Be("https://mcpal.example.com");
        config.Mcpal.ApiKey.Should().Be("mcpal_cccccccc_key");
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

        var act = () => BridgeConfigLoader.Parse(WithVariables, environment, requireMcpal: true);

        act.Should().Throw<BridgeConfigException>().WithMessage("Server 'jira': environment variable 'JIRA_TOKEN' is not set.");
    }

    [Test]
    public void Parse_UnsetVariableInMcpalKey_Throws()
    {
        var environment = AllVariables();
        environment.Remove("MCPAL_TEST_KEY");

        var act = () => BridgeConfigLoader.Parse(WithVariables, environment, requireMcpal: true);

        act.Should().Throw<BridgeConfigException>().WithMessage("*'MCPAL_TEST_KEY' is not set*");
    }

    [Test]
    public void Parse_ApiKeyFromEnvironmentOverride_DoesNotNeedVariableOfConfiguredKey()
    {
        var environment = AllVariables();
        environment.Remove("MCPAL_TEST_KEY");
        environment["MCPAL_API_KEY"] = "mcpal_dddddddd_override";

        var config = BridgeConfigLoader.Parse(WithVariables, environment, requireMcpal: true);

        config.Mcpal.ApiKey.Should().Be("mcpal_dddddddd_override");
    }

    [Test]
    public void Parse_McpalNotRequired_DoesNotFailOnUnsetMcpalVariables()
    {
        var environment = AllVariables();
        environment.Remove("MCPAL_TEST_URL");
        environment.Remove("MCPAL_TEST_KEY");

        var config = BridgeConfigLoader.Parse(WithVariables, environment, requireMcpal: false);

        config.McpServers.Should().ContainKey("db");
    }

    [Test]
    public void Parse_DefaultInConfig_IsUsedWhenVariableUnset()
    {
        const string json = """{ "mcpServers": { "a": { "command": "run", "args": ["${MODE:-fast}"] } } }""";

        var config = BridgeConfigLoader.Parse(json, NoEnvironment, requireMcpal: false);

        config.McpServers["a"].Args.Should().Equal("fast");
    }

    [Test]
    public void Parse_IncludeAndExcludeTools_AreRead()
    {
        const string json = """{ "mcpServers": { "pg": { "command": "x", "includeTools": ["query", "list_*"], "excludeTools": ["drop_*"] } } }""";

        var server = BridgeConfigLoader.Parse(json, NoEnvironment, requireMcpal: false).McpServers["pg"];

        server.IncludeTools.Should().Equal("query", "list_*");
        server.ExcludeTools.Should().Equal("drop_*");
    }

    [Test]
    public void Parse_NoToolLists_GivesEmptyLists()
    {
        const string json = """{ "mcpServers": { "pg": { "command": "x" } } }""";

        var server = BridgeConfigLoader.Parse(json, NoEnvironment, requireMcpal: false).McpServers["pg"];

        server.IncludeTools.Should().BeEmpty();
        server.ExcludeTools.Should().BeEmpty();
    }

    [TestCase("""{ "mcpServers": { "pg": { "command": "x", "includeTools": [""] } } }""")]
    [TestCase("""{ "mcpServers": { "pg": { "command": "x", "excludeTools": ["  "] } } }""")]
    public void Parse_EmptyPattern_ThrowsNamingTheServer(string json)
    {
        var act = () => BridgeConfigLoader.Parse(json, NoEnvironment, requireMcpal: false);

        act.Should().Throw<BridgeConfigException>().WithMessage("Server 'pg'*");
    }

    [Test]
    public void Parse_StatusFile_IsReadAndExpanded()
    {
        var environment = new Dictionary<string, string?> { ["STATE_DIR"] = "/var/lib/mcpal" };
        const string json = """{ "statusFile": "${STATE_DIR}/status.json", "mcpServers": {} }""";

        var config = BridgeConfigLoader.Parse(json, environment, requireMcpal: false);

        config.StatusFile.Should().Be("/var/lib/mcpal/status.json");
    }

    [Test]
    public void Parse_NoStatusFile_IsNull()
    {
        BridgeConfigLoader.Parse("""{ "mcpServers": {} }""", NoEnvironment, requireMcpal: false).StatusFile.Should().BeNull();
    }
}
