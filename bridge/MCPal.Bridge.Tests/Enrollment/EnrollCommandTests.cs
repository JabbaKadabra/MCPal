using System.Net;
using MCPal.Bridge.Config;
using MCPal.Bridge.Enrollment;

namespace MCPal.Bridge.Tests.Enrollment;

[TestFixture]
internal sealed class EnrollCommandTests
{
    private const string DockerLikeConfig = """{ "mcpal": { "url": "${MCPAL_URL}", "bridgeName": "hq-01" } }""";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static (string Directory, string Config) NewConfig(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcpal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "mcpal.json");
        File.WriteAllText(config, json);
        return (directory, config);
    }

    private static BridgeEnrollerTests.StubHandler Accepting() =>
        new(_ => BridgeEnrollerTests.Json(HttpStatusCode.OK, """{"url":"https://mcpal.example.com","apiKey":"mcpal_aaaaaaaa_new"}"""));

    private static Dictionary<string, string?> Env(string? code = "mcpale_code") => new() { ["MCPAL_URL"] = "https://mcpal.example.com", ["MCPAL_ENROLL"] = code };

    [Test]
    public async Task RunAsync_CodeFromEnvironment_EnrollsAndPrintsWhereTheKeyIs()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            var handler = Accepting();
            using var http = new HttpClient(handler);
            var output = new StringWriter();
            var error = new StringWriter();

            var exit = await EnrollCommand.RunAsync(config, url: null, code: null, Env(), http, output, error, Ct);

            exit.Should().Be(0);
            BridgeCredentials.TryRead(Path.Combine(directory, "credentials.json")).Should().Be("mcpal_aaaaaaaa_new");
            output.ToString().Should().Contain("hq-01").And.Contain("credentials.json");
            error.ToString().Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_UrlAndCodeArguments_BeatEnvironmentAndConfig()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            var handler = Accepting();
            using var http = new HttpClient(handler);

            var exit = await EnrollCommand.RunAsync(config, "https://other.example.com", "mcpale_given", Env(), http, new StringWriter(), new StringWriter(), Ct);

            exit.Should().Be(0);
            handler.Requests[0].Uri.Should().Be(new Uri("https://other.example.com/api/bridge/enroll"));
            handler.Requests[0].Body.Should().Contain("mcpale_given");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_NoCode_ExitsWithUsageError()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            using var http = new HttpClient(Accepting());
            var error = new StringWriter();

            var exit = await EnrollCommand.RunAsync(config, null, null, Env(code: null), http, new StringWriter(), error, Ct);

            exit.Should().Be(2);
            error.ToString().Should().Contain("--code").And.Contain("MCPAL_ENROLL");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_NoUrlAnywhere_ExitsWithUsageError()
    {
        var (directory, config) = NewConfig("{}");
        try
        {
            using var http = new HttpClient(Accepting());
            var error = new StringWriter();

            var exit = await EnrollCommand.RunAsync(config, null, null, new Dictionary<string, string?> { ["MCPAL_ENROLL"] = "mcpale_code" }, http, new StringWriter(), error, Ct);

            exit.Should().Be(2);
            error.ToString().Should().Contain("--url");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_InvalidCode_PrintsTheMessageAndExitsWithOne()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            using var http = new HttpClient(new BridgeEnrollerTests.StubHandler(_ => BridgeEnrollerTests.Json(HttpStatusCode.BadRequest, """{"error":"invalid_code"}""")));
            var error = new StringWriter();

            var exit = await EnrollCommand.RunAsync(config, null, null, Env(), http, new StringWriter(), error, Ct);

            exit.Should().Be(1);
            error.ToString().Should().Contain(BridgeEnroller.InvalidCodeMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task EnrollWhenNeededAsync_NoCodeVariable_DoesNothing()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            var handler = Accepting();
            using var http = new HttpClient(handler);

            var exit = await EnrollCommand.EnrollWhenNeededAsync(config, Env(code: null), http, new StringWriter(), new StringWriter(), Ct);

            exit.Should().Be(0);
            handler.Requests.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task EnrollWhenNeededAsync_KeyAlreadyStored_DoesNotCallTheServerAgain()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            BridgeCredentials.Write(Path.Combine(directory, "credentials.json"), "mcpal_aaaaaaaa_stored");
            var handler = Accepting();
            using var http = new HttpClient(handler);

            var exit = await EnrollCommand.EnrollWhenNeededAsync(config, Env(), http, new StringWriter(), new StringWriter(), Ct);

            exit.Should().Be(0);
            handler.Requests.Should().BeEmpty();
            BridgeCredentials.TryRead(Path.Combine(directory, "credentials.json")).Should().Be("mcpal_aaaaaaaa_stored");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task EnrollWhenNeededAsync_KeyInEnvironment_DoesNotCallTheServer()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            var handler = Accepting();
            using var http = new HttpClient(handler);
            var environment = Env();
            environment["MCPAL_API_KEY"] = "mcpal_bbbbbbbb_env";

            var exit = await EnrollCommand.EnrollWhenNeededAsync(config, environment, http, new StringWriter(), new StringWriter(), Ct);

            exit.Should().Be(0);
            handler.Requests.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task EnrollWhenNeededAsync_NoKeyAndCode_EnrollsAndSavesTheKey()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            using var http = new HttpClient(Accepting());

            var exit = await EnrollCommand.EnrollWhenNeededAsync(config, Env(), http, new StringWriter(), new StringWriter(), Ct);

            exit.Should().Be(0);
            BridgeConfigLoader.Load(config, Env(), requireMcpal: true).Mcpal.ApiKey.Should().Be("mcpal_aaaaaaaa_new");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task EnrollWhenNeededAsync_NoKeyAndSpentCode_FailsWithTheInvalidCodeMessage()
    {
        var (directory, config) = NewConfig(DockerLikeConfig);
        try
        {
            using var http = new HttpClient(new BridgeEnrollerTests.StubHandler(_ => BridgeEnrollerTests.Json(HttpStatusCode.BadRequest, """{"error":"invalid_code"}""")));
            var error = new StringWriter();

            var exit = await EnrollCommand.EnrollWhenNeededAsync(config, Env(), http, new StringWriter(), error, Ct);

            exit.Should().Be(1);
            error.ToString().Should().Contain(BridgeEnroller.InvalidCodeMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
