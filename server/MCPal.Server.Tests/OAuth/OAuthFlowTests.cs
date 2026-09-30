using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;

namespace MCPal.Server.Tests.OAuth;

[TestFixture]
internal sealed class OAuthFlowTests
{
    private const string ClaudeRedirect = "https://claude.ai/api/mcp/auth_callback";
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private static async Task<string> RegisterClientAsync(HttpClient http, string redirect = ClaudeRedirect)
    {
        using var response = await http.PostAsJsonAsync("/oauth/register", new { client_name = "Claude", redirect_uris = new[] { redirect }, token_endpoint_auth_method = "none" }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("client_id").GetString() ?? string.Empty;
    }

    private static async Task<HttpResponseMessage> AuthorizeAsync(HttpClient http, string clientId, string challenge, string apiKey, string redirect = ClaudeRedirect, string state = "st4te", string? resource = null, string method = "S256")
    {
        return await http.PostAsJsonAsync("/api/oauth/authorize", new
        {
            client_id = clientId,
            redirect_uri = redirect,
            response_type = "code",
            code_challenge = challenge,
            code_challenge_method = method,
            state,
            resource,
            api_key = apiKey,
        }, Ct);
    }

    private static async Task<string> RedirectUrlAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("redirectUrl").GetString() ?? string.Empty;
    }

    private static string QueryValue(string url, string key) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query).TryGetValue(key, out var value) ? value.ToString() : string.Empty;

    private static Task<HttpResponseMessage> TokenAsync(HttpClient http, Dictionary<string, string> form) =>
        http.PostAsync("/oauth/token", new FormUrlEncodedContent(form), Ct);

    private static async Task<JsonElement> JsonBodyAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.Clone();
    }

    private static async Task<(string Code, string Verifier, string ClientId)> ObtainCodeAsync(HttpClient http, string apiKey)
    {
        var clientId = await RegisterClientAsync(http);
        var (verifier, challenge) = Pkce();
        var redirectUrl = await RedirectUrlAsync(await AuthorizeAsync(http, clientId, challenge, apiKey));
        return (QueryValue(redirectUrl, "code"), verifier, clientId);
    }

    private static Dictionary<string, string> CodeForm(string clientId, string code, string verifier, string redirect = ClaudeRedirect) => new()
    {
        ["grant_type"] = "authorization_code",
        ["client_id"] = clientId,
        ["code"] = code,
        ["redirect_uri"] = redirect,
        ["code_verifier"] = verifier,
    };

    [Test]
    public async Task Metadata_ProtectedResourceAndAuthorizationServer_DescribeEndpoints()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:PublicUrl"] = "https://mcpal.example.com" });
        using var http = factory.CreateClient();

        var resource = await JsonBodyAsync(await http.GetAsync("/.well-known/oauth-protected-resource/mcp", Ct));
        var server = await JsonBodyAsync(await http.GetAsync("/.well-known/oauth-authorization-server", Ct));

        resource.GetProperty("resource").GetString().Should().Be("https://mcpal.example.com/mcp");
        resource.GetProperty("authorization_servers")[0].GetString().Should().Be("https://mcpal.example.com");
        server.GetProperty("issuer").GetString().Should().Be("https://mcpal.example.com");
        server.GetProperty("token_endpoint").GetString().Should().Be("https://mcpal.example.com/oauth/token");
        server.GetProperty("registration_endpoint").GetString().Should().Be("https://mcpal.example.com/oauth/register");
        server.GetProperty("code_challenge_methods_supported")[0].GetString().Should().Be("S256");
    }

    [TestCase("https://evil.example/callback")]
    [TestCase("http://evil.example/callback")]
    public async Task Register_DisallowedRedirectUri_Returns400(string redirect)
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var http = factory.CreateClient();

        using var response = await http.PostAsJsonAsync("/oauth/register", new { client_name = "x", redirect_uris = new[] { redirect } }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonBodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_redirect_uri");
    }

    [Test]
    public async Task Register_LoopbackRedirect_IsAccepted()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var http = factory.CreateClient();

        var clientId = await RegisterClientAsync(http, "http://127.0.0.1:33333/callback");

        clientId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task FullFlow_RegisterAuthorizeTokenCallRefreshRevoke_WorksEndToEnd()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        await using var bridge = await FakeBridge.StartAsync(factory, acme.RawKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("found")), Ct);

        var (code, verifier, clientId) = await ObtainCodeAsync(http, acme.RawKey);
        var tokenResponse = await TokenAsync(http, CodeForm(clientId, code, verifier));
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await JsonBodyAsync(tokenResponse);
        var accessToken = tokens.GetProperty("access_token").GetString() ?? string.Empty;
        var refreshToken = tokens.GetProperty("refresh_token").GetString() ?? string.Empty;
        tokens.GetProperty("token_type").GetString().Should().Be("Bearer");
        tokens.GetProperty("expires_in").GetInt32().Should().Be(3600);

        await using (var client = await ConnectMcpAsync(factory, accessToken))
        {
            (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Should().Equal("kb__search");
            var call = await client.CallToolAsync("kb__search", cancellationToken: Ct);
            call.IsError.Should().NotBe(true);
        }

        var refreshed = await TokenAsync(http, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refreshToken });
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        var newTokens = await JsonBodyAsync(refreshed);
        var newAccess = newTokens.GetProperty("access_token").GetString() ?? string.Empty;
        newAccess.Should().NotBe(accessToken);

        var reused = await TokenAsync(http, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refreshToken });
        reused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonBodyAsync(reused)).GetProperty("error").GetString().Should().Be("invalid_grant");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IApiKeyService>().RevokeAsync(acme.CompanyId, acme.ApiKeyId, Ct);
        }

        using var afterRevoke = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        afterRevoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newAccess);
        using var revokedResponse = await http.SendAsync(afterRevoke, Ct);
        revokedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Authorize_InvalidApiKey_Returns401InvalidKey()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var http = factory.CreateClient();
        var clientId = await RegisterClientAsync(http);

        using var response = await AuthorizeAsync(http, clientId, Pkce().Challenge, "mcpal_00000000_" + new string('a', 40));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await JsonBodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_key");
    }

    [Test]
    public async Task Authorize_UnknownClient_Returns400WithoutRedirect()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();

        using var response = await AuthorizeAsync(http, "does-not-exist", Pkce().Challenge, acme.RawKey);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonBodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_client");
    }

    [Test]
    public async Task Authorize_RedirectUriNotRegistered_Returns400WithoutRedirect()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        var clientId = await RegisterClientAsync(http);

        using var response = await AuthorizeAsync(http, clientId, Pkce().Challenge, acme.RawKey, redirect: "http://localhost:9999/cb");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Authorize_PlainPkceMethod_RedirectsWithInvalidRequest()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        var clientId = await RegisterClientAsync(http);

        var url = await RedirectUrlAsync(await AuthorizeAsync(http, clientId, "plain-challenge", acme.RawKey, method: "plain"));

        QueryValue(url, "error").Should().Be("invalid_request");
        QueryValue(url, "state").Should().Be("st4te");
        QueryValue(url, "code").Should().BeEmpty();
    }

    [Test]
    public async Task Authorize_ResourceOfOtherServer_RedirectsWithInvalidTarget()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        var clientId = await RegisterClientAsync(http);

        var url = await RedirectUrlAsync(await AuthorizeAsync(http, clientId, Pkce().Challenge, acme.RawKey, resource: "https://other.example/mcp"));

        QueryValue(url, "error").Should().Be("invalid_target");
    }

    [Test]
    public async Task Authorize_ResourceMatchingPublicUrl_IsAccepted()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        var clientId = await RegisterClientAsync(http);

        var url = await RedirectUrlAsync(await AuthorizeAsync(http, clientId, Pkce().Challenge, acme.RawKey, resource: "http://localhost:8080/mcp"));

        QueryValue(url, "code").Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Token_WrongCodeVerifier_ReturnsInvalidGrant()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        var (code, _, clientId) = await ObtainCodeAsync(http, acme.RawKey);

        using var response = await TokenAsync(http, CodeForm(clientId, code, Pkce().Verifier));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonBodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Test]
    public async Task Token_CodeUsedTwice_SecondExchangeFails()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        var (code, verifier, clientId) = await ObtainCodeAsync(http, acme.RawKey);

        using var first = await TokenAsync(http, CodeForm(clientId, code, verifier));
        using var second = await TokenAsync(http, CodeForm(clientId, code, verifier));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Token_CodeOfOtherClient_ReturnsInvalidGrant()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        var (code, verifier, _) = await ObtainCodeAsync(http, acme.RawKey);
        var otherClient = await RegisterClientAsync(http);

        using var response = await TokenAsync(http, CodeForm(otherClient, code, verifier));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Token_ExpiredCode_ReturnsInvalidGrant()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, timeProvider: time);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        var (code, verifier, clientId) = await ObtainCodeAsync(http, acme.RawKey);

        time.Advance(TimeSpan.FromMinutes(6));
        using var response = await TokenAsync(http, CodeForm(clientId, code, verifier));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task McpEndpoint_ExpiredAccessToken_Returns401()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, timeProvider: time);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        var (code, verifier, clientId) = await ObtainCodeAsync(http, acme.RawKey);
        var tokens = await JsonBodyAsync(await TokenAsync(http, CodeForm(clientId, code, verifier)));

        time.Advance(TimeSpan.FromMinutes(61));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        using var response = await http.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
    }

    [Test]
    public async Task Token_RefreshWithUnknownToken_ReturnsInvalidGrant()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var http = factory.CreateClient();
        var clientId = await RegisterClientAsync(http);

        using var response = await TokenAsync(http, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = "nope" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonBodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Test]
    public async Task Token_JsonBody_IsRejected()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var http = factory.CreateClient();

        using var response = await http.PostAsJsonAsync("/oauth/token", new { grant_type = "refresh_token" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Tunnel_OAuthAccessToken_IsNotAcceptedOnBridgeHub()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var http = factory.CreateClient();
        var (code, verifier, clientId) = await ObtainCodeAsync(http, acme.RawKey);
        var tokens = await JsonBodyAsync(await TokenAsync(http, CodeForm(clientId, code, verifier)));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/hub/bridge/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        using var response = await http.SendAsync(request, Ct);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task AuthorizeContext_ValidRequest_ReturnsClientName()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var http = factory.CreateClient();
        var clientId = await RegisterClientAsync(http);
        var (_, challenge) = Pkce();

        using var response = await http.GetAsync(
            $"/api/oauth/authorize/context?client_id={clientId}&redirect_uri={Uri.EscapeDataString(ClaudeRedirect)}&response_type=code&code_challenge={challenge}&code_challenge_method=S256&state=x", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonBodyAsync(response)).GetProperty("clientName").GetString().Should().Be("Claude");
    }

    private static async Task<McpClient> ConnectMcpAsync(ServerWebApplicationFactory factory, string bearer)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(factory.Server.BaseAddress, "mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer },
            },
            factory.CreateClient(),
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }
}
