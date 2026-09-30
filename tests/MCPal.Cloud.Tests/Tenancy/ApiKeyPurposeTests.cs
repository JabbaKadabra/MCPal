using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MCPal.Cloud.OAuth;
using MCPal.Cloud.Storage;
using MCPal.Cloud.Tenancy;
using MCPal.Cloud.Tests.Infrastructure;
using MCPal.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace MCPal.Cloud.Tests.Tenancy;

/// <summary>Purpose (agent / client / any) and server restrictions of API keys, on the tunnel, on /mcp and through OAuth.</summary>
[TestFixture]
internal sealed class ApiKeyPurposeTests
{
    private static readonly string[] ClaudeRedirects = ["https://claude.ai/api/mcp/auth_callback"];

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static async Task<CreatedApiKey> CreateKeyAsync(CloudWebApplicationFactory factory, Guid companyId, ApiKeyPurpose purpose, params string[] allowedServers)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IApiKeyService>().CreateAsync(companyId, purpose.ToString(), null, purpose, allowedServers, Ct);
    }

    private static async Task<McpClient> ConnectAsync(CloudWebApplicationFactory factory, string bearer)
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

    private static async Task<HttpStatusCode> PostMcpAsync(CloudWebApplicationFactory factory, string bearer)
    {
        using var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await http.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private static async Task<string> IssueAccessTokenAsync(CloudWebApplicationFactory factory, Guid companyId, Guid apiKeyId)
    {
        var token = "oauth-test-token-" + Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MCPalDbContext>();
        db.OAuthTokens.Add(new OAuthToken
        {
            Hash = ApiKeyService.Hash(token),
            Kind = OAuthTokenKind.Access,
            CompanyId = companyId,
            ApiKeyId = apiKeyId,
            ClientId = "claude",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync(Ct);
        return token;
    }

    private static Task<CallToolResponse> Answer(string text) => Task.FromResult(FakeAgent.Text(text));

    private static AgentCatalog TwoServers() => new("fake", "1.0", ProtocolVersion.Current,
    [
        new ServerCatalog("jira", [new ToolDescriptor("search", null, "Find", "{\"type\":\"object\"}", null)]),
        new ServerCatalog("hr", [new ToolDescriptor("salaries", null, "Secret", "{\"type\":\"object\"}", null)]),
    ]);

    [Test]
    public async Task Tunnel_ClientKey_IsRefused()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var clientKey = await CreateKeyAsync(factory, acme.CompanyId, ApiKeyPurpose.Client);
        await using var connection = factory.CreateAgentConnection(clientKey.RawKey);

        var act = async () => await connection.StartAsync(Ct);

        var error = await act.Should().ThrowAsync<HttpRequestException>();
        error.Which.Message.Should().Contain("403");
    }

    [TestCase(ApiKeyPurpose.Agent)]
    [TestCase(ApiKeyPurpose.Any)]
    public async Task Tunnel_AgentAndAnyKeys_AreAccepted(ApiKeyPurpose purpose)
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var key = await CreateKeyAsync(factory, acme.CompanyId, purpose);
        await using var agent = await FakeAgent.StartAsync(factory, key.RawKey, FakeAgent.CatalogWith("kb", "search"), _ => Answer("x"), Ct);

        agent.RegisterResult?.Accepted.Should().BeTrue();
    }

    [Test]
    public async Task Mcp_AgentKey_IsRefusedWithForbidden()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var agentKey = await CreateKeyAsync(factory, acme.CompanyId, ApiKeyPurpose.Agent);

        var status = await PostMcpAsync(factory, agentKey.RawKey);

        status.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestCase(ApiKeyPurpose.Client)]
    [TestCase(ApiKeyPurpose.Any)]
    public async Task Mcp_ClientAndAnyKeys_AreAccepted(ApiKeyPurpose purpose)
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var key = await CreateKeyAsync(factory, acme.CompanyId, purpose);
        await using var client = await ConnectAsync(factory, key.RawKey);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        tools.Should().BeEmpty();
    }

    [Test]
    public async Task ListTools_KeyRestrictedToServer_ListsOnlyThatServer()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var restricted = await CreateKeyAsync(factory, acme.CompanyId, ApiKeyPurpose.Client, "jira");
        var unrestricted = await CreateKeyAsync(factory, acme.CompanyId, ApiKeyPurpose.Client);
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, TwoServers(), _ => Answer("x"), Ct);
        await using var restrictedClient = await ConnectAsync(factory, restricted.RawKey);
        await using var unrestrictedClient = await ConnectAsync(factory, unrestricted.RawKey);

        var restrictedTools = await restrictedClient.ListToolsAsync(cancellationToken: Ct);
        var allTools = await unrestrictedClient.ListToolsAsync(cancellationToken: Ct);

        restrictedTools.Select(t => t.Name).Should().Equal("jira__search");
        allTools.Select(t => t.Name).Should().BeEquivalentTo("jira__search", "hr__salaries");
    }

    [Test]
    public async Task ListTools_ServerRestrictionIsCaseInsensitive()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var restricted = await CreateKeyAsync(factory, acme.CompanyId, ApiKeyPurpose.Client, "JIRA");
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, TwoServers(), _ => Answer("x"), Ct);
        await using var client = await ConnectAsync(factory, restricted.RawKey);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        tools.Select(t => t.Name).Should().Equal("jira__search");
    }

    [Test]
    public async Task CallTool_ServerOutsideScope_ReturnsNotAvailableAndNeverReachesTheAgent()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var restricted = await CreateKeyAsync(factory, acme.CompanyId, ApiKeyPurpose.Client, "jira");
        var reachedTools = new List<string>();
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, TwoServers(), request =>
        {
            reachedTools.Add(request.ToolName);
            return Answer("data");
        }, Ct);
        await using var client = await ConnectAsync(factory, restricted.RawKey);

        var forbidden = await client.CallToolAsync("hr__salaries", cancellationToken: Ct);
        var unknown = await client.CallToolAsync("nope__nothing", cancellationToken: Ct);
        var allowed = await client.CallToolAsync("jira__search", cancellationToken: Ct);

        forbidden.IsError.Should().BeTrue();
        forbidden.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text.Should().Be(
            unknown.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text.Replace("nope__nothing", "hr__salaries", StringComparison.Ordinal));
        allowed.IsError.Should().NotBe(true);
        reachedTools.Should().Equal("search");
    }

    [Test]
    public async Task Mcp_OAuthTokenIssuedFromRestrictedKey_SeesOnlyAllowedServers()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var restricted = await CreateKeyAsync(factory, acme.CompanyId, ApiKeyPurpose.Client, "jira");
        var token = await IssueAccessTokenAsync(factory, acme.CompanyId, restricted.Id);
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, TwoServers(), _ => Answer("x"), Ct);
        await using var client = await ConnectAsync(factory, token);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var forbidden = await client.CallToolAsync("hr__salaries", cancellationToken: Ct);

        tools.Select(t => t.Name).Should().Equal("jira__search");
        forbidden.IsError.Should().BeTrue();
    }

    [Test]
    public async Task Mcp_OAuthTokenOfAgentKey_IsRefused()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var agentKey = await CreateKeyAsync(factory, acme.CompanyId, ApiKeyPurpose.Agent);
        var token = await IssueAccessTokenAsync(factory, acme.CompanyId, agentKey.Id);

        var status = await PostMcpAsync(factory, token);

        status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Authorize_AgentKey_IsRefusedWithClearMessage()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var agentKey = await CreateKeyAsync(factory, acme.CompanyId, ApiKeyPurpose.Agent);
        using var http = factory.CreateClient();
        var clientId = await RegisterClientAsync(http);

        using var response = await AuthorizeAsync(http, clientId, agentKey.RawKey);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("error").GetString().Should().Be("key_not_allowed");
        body.GetProperty("error_description").GetString().Should().Be("This key can only be used by an agent.");
    }

    [Test]
    public async Task Authorize_ClientKey_IssuesCode()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var clientKey = await CreateKeyAsync(factory, acme.CompanyId, ApiKeyPurpose.Client, "jira");
        using var http = factory.CreateClient();
        var clientId = await RegisterClientAsync(http);

        using var response = await AuthorizeAsync(http, clientId, clientKey.RawKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<string> RegisterClientAsync(HttpClient http)
    {
        using var response = await http.PostAsJsonAsync("/oauth/register", new { client_name = "Claude", redirect_uris = ClaudeRedirects, token_endpoint_auth_method = "none" }, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("client_id").GetString() ?? string.Empty;
    }

    private static async Task<HttpResponseMessage> AuthorizeAsync(HttpClient http, string clientId, string apiKey) =>
        await http.PostAsJsonAsync("/api/oauth/authorize", new
        {
            client_id = clientId,
            redirect_uri = "https://claude.ai/api/mcp/auth_callback",
            response_type = "code",
            code_challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            code_challenge_method = "S256",
            state = "st4te",
            api_key = apiKey,
        }, Ct);
}
