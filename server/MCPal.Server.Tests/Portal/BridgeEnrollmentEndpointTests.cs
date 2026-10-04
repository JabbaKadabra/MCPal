using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;

namespace MCPal.Server.Tests.Portal;

[TestFixture]
internal sealed class BridgeEnrollmentEndpointTests
{
    private const string PublicUrl = "https://mcpal.example.com";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static Dictionary<string, string?> Settings() => new() { ["Mcpal:PublicUrl"] = PublicUrl };

    private static async Task<PortalClient> SignedUpAsync(ServerWebApplicationFactory factory, string company = "Acme", string email = "admin@acme.example")
    {
        var portal = new PortalClient(factory);
        using var response = await portal.SignupAsync(company, email, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return portal;
    }

    private static async Task<string> CreateCodeAsync(PortalClient portal)
    {
        using var response = await portal.PostAsync("/api/portal/setup/enrollments", null, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await PortalClient.JsonAsync(response, Ct)).GetProperty("code").GetString() ?? string.Empty;
    }

    private static async Task<HttpResponseMessage> EnrollAsync(HttpClient client, string? code, string bridgeName = "hq-01") =>
        await client.PostAsJsonAsync("/api/bridge/enroll", new { code, bridgeName }, Ct);

    [Test]
    public async Task CreateEnrollment_Owner_ReturnsCodeAndExpiry()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);

        using var response = await portal.PostAsync("/api/portal/setup/enrollments", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await PortalClient.JsonAsync(response, Ct);
        body.GetProperty("code").GetString().Should().StartWith("mcpale_");
        body.GetProperty("expiresAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task CreateEnrollment_Member_IsForbidden()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        var company = await factory.SeedCompanyAsync("Globex", Ct);
        await factory.SeedUserAsync(company.CompanyId, "member@globex.example", PortalRole.Member, Ct);
        using var member = new PortalClient(factory);
        using var login = await member.PostAsync("/api/portal/auth/login", new { email = "member@globex.example", password = PortalClient.Password }, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await member.PostAsync("/api/portal/setup/enrollments", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CreateEnrollment_NotSignedIn_IsUnauthorized()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = new PortalClient(factory);

        using var response = await portal.PostAsync("/api/portal/setup/enrollments", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task CreateEnrollment_WithoutAntiforgeryToken_IsRejected()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);

        using var response = await portal.PostAsync("/api/portal/setup/enrollments", null, Ct, withCsrf: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateEnrollment_EleventhOpenCode_IsConflict()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);
        for (var i = 0; i < BridgeEnrollmentService.MaxOpenCodes; i++)
        {
            await CreateCodeAsync(portal);
        }

        using var response = await portal.PostAsync("/api/portal/setup/enrollments", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("errors")[0].GetString().Should().Contain("unused enrollment codes");
    }

    [Test]
    public async Task Enroll_ValidCode_ReturnsPublicUrlAndAKeyThatOpensTheTunnel()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);
        var code = await CreateCodeAsync(portal);
        using var anonymous = factory.CreateClient();

        using var response = await EnrollAsync(anonymous, code);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await PortalClient.JsonAsync(response, Ct);
        body.GetProperty("url").GetString().Should().Be(PublicUrl);
        var apiKey = body.GetProperty("apiKey").GetString() ?? string.Empty;
        apiKey.Should().StartWith("mcpal_");
        await using var bridge = await FakeBridge.StartAsync(factory, apiKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        using var setup = await portal.GetAsync("/api/portal/setup", Ct);
        (await PortalClient.JsonAsync(setup, Ct)).GetProperty("hasConnectedBridge").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task Enroll_ValidCode_KeyAppearsInTheKeyListOfTheCodesCompanyOnly()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var acme = await SignedUpAsync(factory);
        using var globex = await SignedUpAsync(factory, "Globex", "admin@globex.example");
        var code = await CreateCodeAsync(acme);
        using var anonymous = factory.CreateClient();
        (await EnrollAsync(anonymous, code, "hq-01")).Dispose();

        using var acmeKeys = await acme.GetAsync("/api/portal/keys", Ct);
        using var globexKeys = await globex.GetAsync("/api/portal/keys", Ct);

        (await PortalClient.JsonAsync(acmeKeys, Ct)).EnumerateArray().Select(k => k.GetProperty("name").GetString()).Should().Contain("Bridge hq-01");
        (await PortalClient.JsonAsync(globexKeys, Ct)).EnumerateArray().Select(k => k.GetProperty("name").GetString()).Should().NotContain("Bridge hq-01");
    }

    [Test]
    public async Task Enroll_SameCodeTwice_SecondIsInvalidCode()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);
        var code = await CreateCodeAsync(portal);
        using var anonymous = factory.CreateClient();
        (await EnrollAsync(anonymous, code)).Dispose();

        using var second = await EnrollAsync(anonymous, code);

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PortalClient.JsonAsync(second, Ct)).GetProperty("error").GetString().Should().Be("invalid_code");
    }

    [TestCase("mcpale_unknownunknownunknown1")]
    [TestCase("")]
    [TestCase(null)]
    public async Task Enroll_BadCode_IsInvalidCodeJson(string? code)
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var anonymous = factory.CreateClient();

        using var response = await EnrollAsync(anonymous, code);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("error").GetString().Should().Be("invalid_code");
    }

    [TestCase("")]
    [TestCase("not json")]
    public async Task Enroll_EmptyOrInvalidBody_Is400NotHtmlAndNotAServerError(string body)
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsync("/api/bridge/enroll", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
    }

    [Test]
    public async Task Enroll_MoreThanTwentyRequestsPerMinute_IsRateLimited()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var anonymous = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 25; i++)
        {
            using var response = await EnrollAsync(anonymous, "mcpale_unknownunknownunknown1");
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
        statuses.Take(20).Should().OnlyContain(s => s == HttpStatusCode.BadRequest);
    }
}
