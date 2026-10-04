using System.Net.Http.Json;
using System.Text.Json;
using MCPal.Contracts;
using MCPal.Server.Access.UserContext;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;

namespace MCPal.Server.Tests.Mcp;

/// <summary>The caller reaches the bridge as a signed token and claims, only for bridges that speak protocol 1.2.</summary>
[TestFixture]
internal sealed class UserContextRelayTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static async Task<McpClient> ConnectAsync(ServerWebApplicationFactory factory, string bearer)
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

    private static async Task<TokenValidationResult> VerifyAsync(ServerWebApplicationFactory factory, string token, string audience)
    {
        using var http = factory.CreateClient();
        var jwks = await http.GetStringAsync("/.well-known/jwks.json", Ct);
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "http://localhost:8080",
            ValidAudience = audience,
            ValidTypes = [UserContextIssuer.TokenType],
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
        };
        return await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
    }

    [Test]
    public async Task CallTool_Bridge12_ReceivesTheCallerWithATokenThatVerifiesAgainstTheJwks()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme Corp", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        CallToolRequest? seen = null;
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("hr", "salaries"), request =>
        {
            seen = request;
            return Task.FromResult(FakeBridge.Text("ok"));
        }, Ct);
        await using var client = await ConnectAsync(factory, anna.PersonalKey);

        await client.CallToolAsync("hr__salaries", cancellationToken: Ct);

        var user = seen?.User;
        user.Should().NotBeNull();
        user.UserId.Should().Be(anna.UserId);
        user.Email.Should().Be("anna@acme.example");
        user.Name.Should().Be("anna@acme.example");
        user.Groups.Should().Equal("Everyone");
        user.CompanyId.Should().Be(acme.CompanyId);
        user.Company.Should().Be("acme-corp");
        var result = await VerifyAsync(factory, user.Token, "mcpal:acme-corp/hr");
        result.IsValid.Should().BeTrue(result.Exception?.Message);
        var jwt = new JsonWebToken(user.Token);
        jwt.Subject.Should().Be(anna.UserId);
        jwt.Id.Should().Be(seen?.RequestId);
        jwt.GetPayloadValue<string>("mcpal_tool").Should().Be("salaries");
        jwt.GetPayloadValue<string>("mcpal_auth").Should().Be("pat");
        jwt.GetPayloadValue<string>("mcpal_role").Should().Be("member");
    }

    [Test]
    public async Task CallTool_Bridge11_ReceivesNoCaller()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        CallToolRequest? seen = null;
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWithProtocol("1.1", "hr", "salaries"), request =>
        {
            seen = request;
            return Task.FromResult(FakeBridge.Text("ok"));
        }, Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var result = await client.CallToolAsync("hr__salaries", cancellationToken: Ct);

        result.IsError.Should().NotBe(true);
        seen.Should().NotBeNull();
        seen.User.Should().BeNull();
    }

    [Test]
    public async Task CallTool_TwoUsers_EachCallCarriesItsOwnIdentityAndAuthKind()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        var oauthToken = await factory.IssueAccessTokenAsync(acme.CompanyId, acme.OwnerUserId, "claude", Ct);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<CallToolRequest>();
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("hr", "salaries"), request =>
        {
            seen.Enqueue(request);
            return Task.FromResult(FakeBridge.Text("ok"));
        }, Ct);
        await using var annaClient = await ConnectAsync(factory, anna.PersonalKey);
        await using var ownerClient = await ConnectAsync(factory, oauthToken);

        await Task.WhenAll(annaClient.CallToolAsync("hr__salaries", cancellationToken: Ct).AsTask(), ownerClient.CallToolAsync("hr__salaries", cancellationToken: Ct).AsTask());

        seen.Should().HaveCount(2);
        var byUser = seen.ToDictionary(r => r.User?.UserId ?? string.Empty);
        byUser.Should().ContainKeys(anna.UserId, acme.OwnerUserId);
        new JsonWebToken(byUser[anna.UserId].User?.Token).GetPayloadValue<string>("mcpal_auth").Should().Be("pat");
        var ownerJwt = new JsonWebToken(byUser[acme.OwnerUserId].User?.Token);
        ownerJwt.GetPayloadValue<string>("mcpal_auth").Should().Be("oauth");
        ownerJwt.GetPayloadValue<string>("mcpal_role").Should().Be("owner");
        byUser[anna.UserId].RequestId.Should().NotBe(byUser[acme.OwnerUserId].RequestId);
    }

    [Test]
    public async Task CallTool_TokenOfOneCompany_DoesNotVerifyForAnotherCompanysAudience()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        CallToolRequest? seen = null;
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("hr", "salaries"), request =>
        {
            seen = request;
            return Task.FromResult(FakeBridge.Text("ok"));
        }, Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        await client.CallToolAsync("hr__salaries", cancellationToken: Ct);

        var token = seen?.User?.Token ?? string.Empty;
        (await VerifyAsync(factory, token, "mcpal:acme/hr")).IsValid.Should().BeTrue();
        (await VerifyAsync(factory, token, "mcpal:globex/hr")).IsValid.Should().BeFalse();
        (await VerifyAsync(factory, token, "mcpal:acme/wiki")).IsValid.Should().BeFalse();
    }

    [Test]
    public async Task Connections_Bridge12AndBridge11_ShowWhetherTheyGetTheCallerAndTheAudienceOfEachServer()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var owner = new PortalClient(factory);
        using var login = await owner.PostAsync("/api/portal/auth/login", new { email = acme.OwnerEmail, password = PortalClient.Password }, Ct);
        var second = await factory.SeedCompanyAsync("Second", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("hr", "salaries"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var old = await FakeBridge.StartAsync(factory, second.BridgeKey, FakeBridge.CatalogWithProtocol("1.1", "wiki", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);

        using var response = await owner.GetAsync("/api/portal/connections", Ct);

        var connection = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.EnumerateArray().Single();
        connection.GetProperty("supportsUserContext").GetBoolean().Should().BeTrue();
        connection.GetProperty("servers")[0].GetProperty("audience").GetString().Should().Be("mcpal:acme/hr");
        using var secondOwner = new PortalClient(factory);
        using var secondLogin = await secondOwner.PostAsync("/api/portal/auth/login", new { email = second.OwnerEmail, password = PortalClient.Password }, Ct);
        using var secondResponse = await secondOwner.GetAsync("/api/portal/connections", Ct);
        JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync(Ct)).RootElement.EnumerateArray().Single().GetProperty("supportsUserContext").GetBoolean().Should().BeFalse();
        login.IsSuccessStatusCode.Should().BeTrue();
    }
}
