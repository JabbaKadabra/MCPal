using System.Net;
using System.Text;
using System.Text.Json;
using MCPal.Bridge.Enrollment;

namespace MCPal.Bridge.Tests.Enrollment;

[TestFixture]
internal sealed class BridgeEnrollerTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static string NewFile() => Path.Combine(Path.GetTempPath(), "mcpal-test-" + Guid.NewGuid().ToString("N"), "credentials.json");

    private static void Cleanup(string file)
    {
        var directory = Path.GetDirectoryName(file);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Test]
    public async Task EnrollAsync_ValidCode_PostsCodeAndNameSavesKeyAndReturnsIt()
    {
        var file = NewFile();
        try
        {
            var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"url":"https://mcpal.example.com","apiKey":"mcpal_aaaaaaaa_new"}"""));
            using var http = new HttpClient(handler);

            var key = await BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com/", "mcpale_code", "hq-01", file, Ct);

            key.Should().Be("mcpal_aaaaaaaa_new");
            BridgeCredentials.TryRead(file).Should().Be("mcpal_aaaaaaaa_new");
            handler.Requests.Should().ContainSingle();
            handler.Requests[0].Uri.Should().Be(new Uri("https://mcpal.example.com/api/bridge/enroll"));
            using var body = JsonDocument.Parse(handler.Requests[0].Body);
            body.RootElement.GetProperty("code").GetString().Should().Be("mcpale_code");
            body.RootElement.GetProperty("bridgeName").GetString().Should().Be("hq-01");
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Test]
    public async Task EnrollAsync_InvalidCode_ThrowsClearMessageAndWritesNoFile()
    {
        var file = NewFile();
        try
        {
            using var http = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_code"}""")));

            var act = () => BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com", "mcpale_code", "hq-01", file, Ct);

            (await act.Should().ThrowAsync<EnrollmentException>()).WithMessage(BridgeEnroller.InvalidCodeMessage);
            File.Exists(file).Should().BeFalse();
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Test]
    public async Task EnrollAsync_TooManyRequests_SaysToWait()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        var act = () => BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com", "mcpale_code", "hq-01", NewFile(), Ct);

        (await act.Should().ThrowAsync<EnrollmentException>()).WithMessage("*wait*");
    }

    [Test]
    public async Task EnrollAsync_ServerError_MentionsStatusAndUrl()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var act = () => BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com", "mcpale_code", "hq-01", NewFile(), Ct);

        (await act.Should().ThrowAsync<EnrollmentException>()).WithMessage("*https://mcpal.example.com/api/bridge/enroll*500*");
    }

    [Test]
    public async Task EnrollAsync_NetworkError_MentionsTheUrl()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var act = () => BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com", "mcpale_code", "hq-01", NewFile(), Ct);

        (await act.Should().ThrowAsync<EnrollmentException>()).WithMessage("*https://mcpal.example.com/api/bridge/enroll*connection refused*");
    }

    [Test]
    public async Task EnrollAsync_AnswerWithoutKey_Throws()
    {
        using var http = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, """{"url":"https://mcpal.example.com"}""")));

        var act = () => BridgeEnroller.EnrollAsync(http, "https://mcpal.example.com", "mcpale_code", "hq-01", NewFile(), Ct);

        (await act.Should().ThrowAsync<EnrollmentException>()).WithMessage("*no key*");
    }
}
