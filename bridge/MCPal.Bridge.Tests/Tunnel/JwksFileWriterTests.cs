using System.Net;
using MCPal.Bridge.Config;
using MCPal.Bridge.Tunnel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Bridge.Tests.Tunnel;

[TestFixture]
internal sealed class JwksFileWriterTests
{
    private const string Jwks = """{"keys":[{"kty":"EC","crv":"P-256","x":"a","y":"b","kid":"k1","use":"sig","alg":"ES256"}]}""";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<Uri?> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uris.Add(request.RequestUri);
            return Task.FromResult(answer(request));
        }
    }

    private static string NewDirectory() => Directory.CreateTempSubdirectory("mcpal-jwks-").FullName;

    private static BridgeConfig ConfigFor(string file) =>
        new(new McpalConfig("https://mcpal.example.com", "mcpal_a_b", "hq"), new Dictionary<string, LocalServerConfig>(), 30) { JwksFile = file };

    private static JwksFileWriter WriterFor(BridgeConfig config, HttpMessageHandler handler, TimeProvider? time = null) =>
        new(config, time ?? new FakeTimeProvider(), NullLogger<JwksFileWriter>.Instance, handler);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Test]
    public async Task FetchOnceAsync_Success_WritesTheJwksFromTheServersWellKnownUrl()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "jwks.json");
        var handler = new Answering(_ => Ok(Jwks));

        await WriterFor(ConfigFor(file), handler).FetchOnceAsync(Ct);

        (await File.ReadAllTextAsync(file, Ct)).Should().Be(Jwks);
        handler.Uris.Should().ContainSingle().Which.Should().Be(new Uri("https://mcpal.example.com/.well-known/jwks.json"));
    }

    [Test]
    public async Task FetchOnceAsync_ExistingFile_IsReplacedAtomicallyWithoutLeavingTempFiles()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "jwks.json");
        await File.WriteAllTextAsync(file, """{"keys":[]}""", Ct);

        await WriterFor(ConfigFor(file), new Answering(_ => Ok(Jwks))).FetchOnceAsync(Ct);

        (await File.ReadAllTextAsync(file, Ct)).Should().Be(Jwks);
        Directory.GetFiles(directory).Should().ContainSingle().Which.Should().Be(file);
    }

    [Test]
    public async Task FetchOnceAsync_MissingDirectory_IsCreated()
    {
        var file = Path.Combine(NewDirectory(), "nested", "dir", "jwks.json");

        await WriterFor(ConfigFor(file), new Answering(_ => Ok(Jwks))).FetchOnceAsync(Ct);

        File.Exists(file).Should().BeTrue();
    }

    [Test]
    public async Task FetchOnceAsync_ServerError_KeepsThePreviousFile()
    {
        var file = Path.Combine(NewDirectory(), "jwks.json");
        await File.WriteAllTextAsync(file, Jwks, Ct);
        var writer = WriterFor(ConfigFor(file), new Answering(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var act = async () => await writer.FetchOnceAsync(Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
        (await File.ReadAllTextAsync(file, Ct)).Should().Be(Jwks);
    }

    [TestCase("<html>SPA</html>")]
    [TestCase("""{"notkeys":[]}""")]
    [TestCase("""{"keys":"nope"}""")]
    [TestCase("")]
    public async Task FetchOnceAsync_AnswerThatIsNoJwks_IsNotWritten(string body)
    {
        var file = Path.Combine(NewDirectory(), "jwks.json");
        await File.WriteAllTextAsync(file, Jwks, Ct);
        var writer = WriterFor(ConfigFor(file), new Answering(_ => Ok(body)));

        var act = async () => await writer.FetchOnceAsync(Ct);

        await act.Should().ThrowAsync<InvalidDataException>();
        (await File.ReadAllTextAsync(file, Ct)).Should().Be(Jwks);
        Directory.GetFiles(Path.GetDirectoryName(file) ?? ".").Should().ContainSingle();
    }

    [Test]
    public async Task StartAsync_FetchesAtStartupAndThenEveryHour()
    {
        var file = Path.Combine(NewDirectory(), "jwks.json");
        var time = new FakeTimeProvider();
        var handler = new Answering(_ => Ok(Jwks));
        using var writer = WriterFor(ConfigFor(file), handler, time);

        await writer.StartAsync(Ct);
        await WaitForAsync(() => handler.Calls >= 1);
        time.Advance(TimeSpan.FromHours(1));
        await WaitForAsync(() => handler.Calls >= 2);
        await writer.StopAsync(Ct);

        handler.Calls.Should().BeGreaterThanOrEqualTo(2);
        File.Exists(file).Should().BeTrue();
    }

    [Test]
    public async Task StartAsync_NoJwksFileConfigured_DoesNothing()
    {
        var handler = new Answering(_ => Ok(Jwks));
        var config = new BridgeConfig(new McpalConfig("https://mcpal.example.com", "k", "hq"), new Dictionary<string, LocalServerConfig>(), 30);
        using var writer = WriterFor(config, handler);

        await writer.StartAsync(Ct);
        await writer.StopAsync(Ct);

        handler.Calls.Should().Be(0);
    }

    [Test]
    public async Task StartAsync_FailingFetch_KeepsRunningAndRetriesNextHour()
    {
        var file = Path.Combine(NewDirectory(), "jwks.json");
        var time = new FakeTimeProvider();
        var failing = true;
        var handler = new Answering(_ => failing ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Ok(Jwks));
        using var writer = WriterFor(ConfigFor(file), handler, time);

        await writer.StartAsync(Ct);
        await WaitForAsync(() => handler.Calls >= 1);
        failing = false;
        time.Advance(TimeSpan.FromHours(1));
        await WaitForAsync(() => File.Exists(file));
        await writer.StopAsync(Ct);

        (await File.ReadAllTextAsync(file, Ct)).Should().Be(Jwks);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
