using MCPal.Bridge.Config;

namespace MCPal.Bridge.Tests.Config;

/// <summary>The local servers can live in their own file in the Claude Code <c>.mcp.json</c> shape, next to <c>mcpal.json</c>.</summary>
[TestFixture]
internal sealed class BridgeConfigServersFileTests
{
    private const string McpalOnly = """{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "k" } }""";

    private const string ClaudeCodeFile = """
        {
          "mcpServers": {
            "kb":   { "type": "stdio", "command": "npx", "args": ["-y", "@acme/kb-mcp"], "env": { "KB_TOKEN": "${KB_TOKEN}" } },
            "wiki": { "type": "http", "url": "http://intranet:8080/mcp", "headers": { "Authorization": "Bearer x" } }
          }
        }
        """;

    private static readonly IReadOnlyDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    /// <summary>Writes <c>mcpal.json</c> and the given extra files into a new directory and returns the path of <c>mcpal.json</c>.</summary>
    private static string WriteConfig(string mcpalJson, params (string Name, string Content)[] files)
    {
        var directory = Directory.CreateTempSubdirectory("mcpal-config-").FullName;
        var path = Path.Combine(directory, "mcpal.json");
        File.WriteAllText(path, mcpalJson);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(directory, name), content);
        }

        return path;
    }

    [Test]
    public void Load_McpJsonNextToConfig_LoadsItsServers()
    {
        var path = WriteConfig(McpalOnly, ("mcp.json", ClaudeCodeFile));

        var config = BridgeConfigLoader.Load(path, new Dictionary<string, string?> { ["KB_TOKEN"] = "secret" }, requireMcpal: true);

        config.McpServers.Keys.Should().BeEquivalentTo("kb", "wiki");
        config.McpServers["kb"].Command.Should().Be("npx");
        config.McpServers["kb"].Env["KB_TOKEN"].Should().Be("secret");
        config.McpServers["wiki"].Url.Should().Be("http://intranet:8080/mcp");
        config.McpServers["wiki"].Headers["Authorization"].Should().Be("Bearer x");
    }

    [Test]
    public void Load_NoServersFile_HasNoServers()
    {
        var path = WriteConfig(McpalOnly);

        BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true).McpServers.Should().BeEmpty();
    }

    [Test]
    public void Load_McpServersFileRelativeToConfig_IsUsed()
    {
        var path = WriteConfig("""{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "k", "mcpServersFile": "servers/claude.json" } }""");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "servers"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "servers", "claude.json"), """{ "mcpServers": { "kb": { "command": "x" } } }""");

        BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true).McpServers.Keys.Should().BeEquivalentTo("kb");
    }

    [Test]
    public void Load_McpServersFileWithAbsolutePath_IsUsed()
    {
        var other = Directory.CreateTempSubdirectory("mcpal-servers-").FullName;
        var servers = Path.Combine(other, "my-servers.json");
        File.WriteAllText(servers, """{ "mcpServers": { "kb": { "command": "x" } } }""");
        var path = WriteConfig($$"""{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "k", "mcpServersFile": {{System.Text.Json.JsonSerializer.Serialize(servers)}} } }""");

        BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true).McpServers.Keys.Should().BeEquivalentTo("kb");
    }

    [Test]
    public void Load_NamedMcpServersFileIsMissing_Fails()
    {
        var path = WriteConfig("""{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "k", "mcpServersFile": "nope.json" } }""");

        var act = () => BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true);

        act.Should().Throw<BridgeConfigException>().WithMessage("*nope.json*");
    }

    [Test]
    public void Load_InlineAndFileServers_AreCombined()
    {
        var path = WriteConfig(
            """{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "k" }, "mcpServers": { "inline": { "command": "a" } } }""",
            ("mcp.json", """{ "mcpServers": { "fromfile": { "command": "b" } } }"""));

        BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true).McpServers.Keys.Should().BeEquivalentTo("inline", "fromfile");
    }

    [Test]
    public void Load_SameServerInlineAndInFile_Fails()
    {
        var path = WriteConfig(
            """{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "k" }, "mcpServers": { "kb": { "command": "a" } } }""",
            ("mcp.json", """{ "mcpServers": { "kb": { "command": "b" } } }"""));

        var act = () => BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true);

        act.Should().Throw<BridgeConfigException>().WithMessage("*'kb'*both*mcp.json*");
    }

    [TestCase("""{ "servers": { "kb": { "command": "x" } } }""")]
    [TestCase("""{ "mcpServers": [] }""")]
    [TestCase("[]")]
    [TestCase("")]
    public void Load_ServersFileWithoutMcpServersBlock_Fails(string content)
    {
        var path = WriteConfig(McpalOnly, ("mcp.json", content));

        var act = () => BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true);

        act.Should().Throw<BridgeConfigException>().WithMessage("*mcp.json*'mcpServers'*");
    }

    [Test]
    public void Load_ServersFileIsNotJson_FailsNamingTheFile()
    {
        var path = WriteConfig(McpalOnly, ("mcp.json", "{ nope"));

        var act = () => BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true);

        act.Should().Throw<BridgeConfigException>().WithMessage("*mcp.json*not valid JSON*");
    }

    [Test]
    public void Load_InvalidServerInFile_FailsLikeAnInlineServer()
    {
        var path = WriteConfig(McpalOnly, ("mcp.json", """{ "mcpServers": { "kb": { } } }"""));

        var act = () => BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true);

        act.Should().Throw<BridgeConfigException>().WithMessage("*Server 'kb' needs 'command'*");
    }

    [Test]
    public void Load_ServerOptions_ApplyToServersFromTheFile()
    {
        var path = WriteConfig(
            """
            {
              "mcpal": { "url": "https://mcpal.example.com", "apiKey": "k" },
              "serverOptions": {
                "wiki": { "userTokenHeader": "Authorization", "includeTools": ["read_*"], "excludeTools": ["read_secret"] },
                "kb": { "userContext": false }
              }
            }
            """,
            ("mcp.json", """{ "mcpServers": { "kb": { "command": "x" }, "wiki": { "url": "http://intranet:8080/mcp" } } }"""));

        var config = BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true);

        config.McpServers["wiki"].UserTokenHeader.Should().Be("Authorization");
        config.McpServers["wiki"].IncludeTools.Should().Equal("read_*");
        config.McpServers["wiki"].ExcludeTools.Should().Equal("read_secret");
        config.McpServers["wiki"].UserContext.Should().BeTrue();
        config.McpServers["kb"].UserContext.Should().BeFalse();
    }

    [Test]
    public void Load_ServerOptionsOverrideInlineSettingsOfTheSameServer()
    {
        var path = WriteConfig("""
            {
              "mcpal": { "url": "https://mcpal.example.com", "apiKey": "k" },
              "mcpServers": { "kb": { "command": "x", "userContext": true } },
              "serverOptions": { "kb": { "userContext": false } }
            }
            """);

        BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true).McpServers["kb"].UserContext.Should().BeFalse();
    }

    [Test]
    public void Load_ServerOptionsForUnknownServer_Fails()
    {
        var path = WriteConfig(
            """{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "k" }, "serverOptions": { "typo": { "userContext": false } } }""",
            ("mcp.json", """{ "mcpServers": { "kb": { "command": "x" } } }"""));

        var act = () => BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true);

        act.Should().Throw<BridgeConfigException>().WithMessage("*serverOptions*'typo'*");
    }

    [Test]
    public void Load_ServerOptionsBreakingARule_FailsLikeInlineSettings()
    {
        var path = WriteConfig(
            """{ "mcpal": { "url": "https://mcpal.example.com", "apiKey": "k" }, "serverOptions": { "kb": { "userTokenHeader": "Authorization" } } }""",
            ("mcp.json", """{ "mcpServers": { "kb": { "command": "x" } } }"""));

        var act = () => BridgeConfigLoader.Load(path, NoEnvironment, requireMcpal: true);

        act.Should().Throw<BridgeConfigException>().WithMessage("*Server 'kb'*userTokenHeader*only for HTTP*");
    }
}
