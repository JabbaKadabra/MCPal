using System.Net;
using System.Net.Http.Headers;
using MCPal.Server.Tests.Infrastructure;
using MCPal.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MCPal.Server.Tests.Portal;

[TestFixture]
internal sealed class PortalApiTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task Signup_ValidRequest_CreatesCompanySignsInAndMeWorks()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);

        using var signup = await portal.SignupAsync("Acme GmbH", "admin@acme.example", Ct);
        using var me = await portal.GetAsync("/api/portal/auth/me", Ct);

        signup.StatusCode.Should().Be(HttpStatusCode.Created);
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await PortalClient.JsonAsync(me, Ct);
        body.GetProperty("email").GetString().Should().Be("admin@acme.example");
        body.GetProperty("companyName").GetString().Should().Be("Acme GmbH");
    }

    [Test]
    public async Task Signup_WithoutAntiforgeryHeader_Returns400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);

        using var response = await portal.PostAsync("/api/portal/auth/signup", new { companyName = "Acme", email = "a@acme.example", password = PortalClient.Password }, Ct, withCsrf: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Signup_WeakPassword_Returns400WithErrors()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);

        using var response = await portal.PostAsync("/api/portal/auth/signup", new { companyName = "Acme", email = "a@acme.example", password = "short" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Test]
    public async Task Signup_WeakPasswordThenRetry_LeavesNoOrphanCompanyAndKeepsSlug()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        (await portal.PostAsync("/api/portal/auth/signup", new { companyName = "Acme", email = "a@acme.example", password = "short" }, Ct)).Dispose();

        using var retry = await portal.SignupAsync("Acme", "a@acme.example", Ct);

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var scope = factory.Services.CreateAsyncScope();
        var companies = await scope.ServiceProvider.GetRequiredService<MCPalDbContext>().Companies.AsNoTracking().ToListAsync(Ct);
        companies.Should().ContainSingle().Which.Slug.Should().Be("acme");
    }

    [Test]
    public async Task Signup_EmailAlreadyRegistered_Returns400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var first = new PortalClient(factory);
        using var second = new PortalClient(factory);
        (await first.SignupAsync("Acme", "admin@acme.example", Ct)).Dispose();

        using var response = await second.SignupAsync("Other", "admin@acme.example", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Me_WithoutSession_Returns401InsteadOfRedirect()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);

        using var response = await portal.GetAsync("/api/portal/auth/me", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Login_CorrectAndWrongPassword_SucceedsOnlyWithCorrect()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var registrar = new PortalClient(factory);
        (await registrar.SignupAsync("Acme", "admin@acme.example", Ct)).Dispose();
        var confirmation = factory.Emails.To("admin@acme.example").Single();
        (await registrar.PostAsync("/api/portal/auth/confirm-email", new { userId = confirmation.QueryValue("userId"), token = confirmation.QueryValue("token") }, Ct)).Dispose();
        using var browser = new PortalClient(factory);

        using var wrong = await browser.PostAsync("/api/portal/auth/login", new { email = "admin@acme.example", password = "wrong-password-1" }, Ct);
        using var right = await browser.PostAsync("/api/portal/auth/login", new { email = "admin@acme.example", password = PortalClient.Password }, Ct);
        using var me = await browser.GetAsync("/api/portal/auth/me", Ct);

        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        right.StatusCode.Should().Be(HttpStatusCode.OK);
        me.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Logout_AfterLogin_EndsSession()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        (await portal.SignupAsync("Acme", "admin@acme.example", Ct)).Dispose();

        (await portal.PostAsync("/api/portal/auth/logout", null, Ct)).Dispose();
        using var me = await portal.GetAsync("/api/portal/auth/me", Ct);

        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Keys_CreateListRevoke_FollowLifecycle()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        (await portal.SignupAsync("Acme", "admin@acme.example", Ct)).Dispose();

        using var created = await portal.PostAsync("/api/portal/keys", new { name = "hq bridge" }, Ct);
        var key = await PortalClient.JsonAsync(created, Ct);
        var rawKey = key.GetProperty("key").GetString() ?? string.Empty;
        var id = key.GetProperty("id").GetGuid();
        using var listed = await portal.GetAsync("/api/portal/keys", Ct);
        var list = await PortalClient.JsonAsync(listed, Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        rawKey.Should().StartWith("mcpal_");
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("name").GetString().Should().Be("hq bridge");
        list[0].GetRawText().Should().NotContain(rawKey);
        (await NegotiateStatusAsync(factory, rawKey)).Should().Be(HttpStatusCode.OK);

        using var revoked = await portal.DeleteAsync($"/api/portal/keys/{id}", Ct);

        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await NegotiateStatusAsync(factory, rawKey)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Keys_OtherCompany_CannotListOrRevokeForeignKeys()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var acme = new PortalClient(factory);
        using var globex = new PortalClient(factory);
        (await acme.SignupAsync("Acme", "admin@acme.example", Ct)).Dispose();
        (await globex.SignupAsync("Globex", "admin@globex.example", Ct)).Dispose();
        using var created = await acme.PostAsync("/api/portal/keys", new { name = "acme key" }, Ct);
        var acmeKey = await PortalClient.JsonAsync(created, Ct);

        using var globexList = await globex.GetAsync("/api/portal/keys", Ct);
        using var globexRevoke = await globex.DeleteAsync($"/api/portal/keys/{acmeKey.GetProperty("id").GetGuid()}", Ct);

        (await PortalClient.JsonAsync(globexList, Ct)).GetArrayLength().Should().Be(0);
        globexRevoke.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await NegotiateStatusAsync(factory, acmeKey.GetProperty("key").GetString() ?? string.Empty)).Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Keys_WithoutSession_Returns401()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);

        using var response = await portal.GetAsync("/api/portal/keys", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Connections_BridgeOnline_ListsBridgeServersAndToolsForOwnCompanyOnly()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var acme = new PortalClient(factory);
        using var globex = new PortalClient(factory);
        (await acme.SignupAsync("Acme", "admin@acme.example", Ct)).Dispose();
        (await globex.SignupAsync("Globex", "admin@globex.example", Ct)).Dispose();
        using var created = await acme.PostAsync("/api/portal/keys", new { name = "hq" }, Ct);
        var rawKey = (await PortalClient.JsonAsync(created, Ct)).GetProperty("key").GetString() ?? string.Empty;
        await using var bridge = await FakeBridge.StartAsync(factory, rawKey, FakeBridge.CatalogWith("kb", "search", "get"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);

        using var acmeConnections = await acme.GetAsync("/api/portal/connections", Ct);
        using var globexConnections = await globex.GetAsync("/api/portal/connections", Ct);

        var list = await PortalClient.JsonAsync(acmeConnections, Ct);
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("bridgeName").GetString().Should().Be("fake");
        list[0].GetProperty("apiKeyName").GetString().Should().Be("hq");
        list[0].GetProperty("servers")[0].GetProperty("name").GetString().Should().Be("kb");
        list[0].GetProperty("servers")[0].GetProperty("tools").GetArrayLength().Should().Be(2);
        list[0].GetProperty("rejectedTools").GetArrayLength().Should().Be(0);
        (await PortalClient.JsonAsync(globexConnections, Ct)).GetArrayLength().Should().Be(0);
    }

    [TestCase("1.2.0", true)]
    [TestCase("1.0.0", false)]
    [TestCase(null, false)]
    public async Task Connections_BridgeVersion_IsShownAndUpdateFlagFollowsLatestBridgeVersion(string? latest, bool updateAvailable)
    {
        var settings = new Dictionary<string, string?>();
        if (latest is not null)
        {
            settings["Mcpal:LatestBridgeVersion"] = latest;
        }

        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: settings);
        using var portal = new PortalClient(factory);
        (await portal.SignupAsync("Acme", "admin@acme.example", Ct)).Dispose();
        using var created = await portal.PostAsync("/api/portal/keys", new { name = "hq" }, Ct);
        var rawKey = (await PortalClient.JsonAsync(created, Ct)).GetProperty("key").GetString() ?? string.Empty;
        await using var bridge = await FakeBridge.StartAsync(factory, rawKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);

        using var connections = await portal.GetAsync("/api/portal/connections", Ct);

        var connection = (await PortalClient.JsonAsync(connections, Ct))[0];
        connection.GetProperty("bridgeVersion").GetString().Should().Be("1.0");
        connection.GetProperty("updateAvailable").GetBoolean().Should().Be(updateAvailable);
        connection.GetProperty("latestBridgeVersion").GetString().Should().Be(latest);
    }

    [Test]
    public async Task ConnectInfo_ReturnsMcpUrlAndClaudeCodeCommand()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:PublicUrl"] = "https://mcpal.example.com" });
        using var portal = new PortalClient(factory);
        (await portal.SignupAsync("Acme", "admin@acme.example", Ct)).Dispose();

        using var response = await portal.GetAsync("/api/portal/connect-info", Ct);

        var info = await PortalClient.JsonAsync(response, Ct);
        info.GetProperty("mcpUrl").GetString().Should().Be("https://mcpal.example.com/mcp");
        info.GetProperty("claudeCodeCommand").GetString().Should().Contain("claude mcp add --transport http mcpal https://mcpal.example.com/mcp");
    }

    [Test]
    public async Task Startup_EmptyDatabaseWithMigrateOnStartup_CreatesSchema()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, migrateOnStartup: true);
        using var portal = new PortalClient(factory);

        using var signup = await portal.SignupAsync("Acme", "admin@acme.example", Ct);

        signup.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Test]
    public async Task Model_MatchesLatestMigration()
    {
        var connectionString = await PostgresFixture.CreateDatabaseAsync(Ct);
        var options = new DbContextOptionsBuilder<MCPalDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new MCPalDbContext(options);

        db.Database.HasPendingModelChanges().Should().BeFalse("every model change needs a migration");
    }

    [Test]
    public async Task OAuthAuthorize_SignedInPortalUser_IssuesCodeBoundToCompanyWithoutKey()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        (await portal.SignupAsync("Acme", "admin@acme.example", Ct)).Dispose();
        var clientId = await RegisterClientAsync(portal);
        var (verifier, challenge) = Pkce();
        var query = $"client_id={clientId}&redirect_uri={Uri.EscapeDataString(ClaudeRedirect)}&response_type=code&code_challenge={challenge}&code_challenge_method=S256&state=s";

        using var context = await portal.GetAsync("/api/oauth/authorize/context?" + query, Ct);
        using var authorize = await portal.PostAsync("/api/oauth/authorize", AuthorizeBody(clientId, challenge, useSession: true), Ct);

        (await PortalClient.JsonAsync(context, Ct)).GetProperty("signedInCompany").GetString().Should().Be("Acme");
        authorize.StatusCode.Should().Be(HttpStatusCode.OK);
        var redirect = new Uri((await PortalClient.JsonAsync(authorize, Ct)).GetProperty("redirectUrl").GetString() ?? string.Empty);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(redirect.Query)["code"].ToString();
        using var anonymous = factory.CreateClient();
        using var token = await anonymous.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = ClaudeRedirect,
            ["code_verifier"] = verifier,
        }), Ct);
        token.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = (await PortalClient.JsonAsync(token, Ct)).GetProperty("access_token").GetString() ?? string.Empty;
        (await NegotiateStatusAsync(factory, access)).Should().NotBe(HttpStatusCode.OK, "OAuth tokens must not open tunnels");
    }

    [Test]
    public async Task OAuthAuthorize_UseSessionWithoutLogin_Returns401LoginRequired()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        var clientId = await RegisterClientAsync(portal);

        using var response = await portal.PostAsync("/api/oauth/authorize", AuthorizeBody(clientId, Pkce().Challenge, useSession: true), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("error").GetString().Should().Be("login_required");
    }

    [Test]
    public async Task OAuthAuthorize_UseSessionWithoutAntiforgeryHeader_Returns400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        (await portal.SignupAsync("Acme", "admin@acme.example", Ct)).Dispose();
        var clientId = await RegisterClientAsync(portal);

        using var response = await portal.PostAsync("/api/oauth/authorize", AuthorizeBody(clientId, Pkce().Challenge, useSession: true), Ct, withCsrf: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private const string ClaudeRedirect = "https://claude.ai/api/mcp/auth_callback";

    private static object AuthorizeBody(string clientId, string challenge, bool useSession) => new
    {
        client_id = clientId,
        redirect_uri = ClaudeRedirect,
        response_type = "code",
        code_challenge = challenge,
        code_challenge_method = "S256",
        state = "s",
        use_session = useSession,
    };

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier));
        return (verifier, Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    }

    private static async Task<string> RegisterClientAsync(PortalClient portal)
    {
        using var response = await portal.PostAsync("/oauth/register", new { client_name = "Claude", redirect_uris = new[] { ClaudeRedirect } }, Ct, withCsrf: false);
        return (await PortalClient.JsonAsync(response, Ct)).GetProperty("client_id").GetString() ?? string.Empty;
    }

    private static async Task<HttpStatusCode> NegotiateStatusAsync(ServerWebApplicationFactory factory, string bearer)
    {
        using var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hub/bridge/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await http.SendAsync(request, Ct);
        return response.StatusCode;
    }
}
