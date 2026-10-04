using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MCPal.Server.Tests.Infrastructure;

namespace MCPal.Server.Tests.Portal;

/// <summary>The owner setup walkthrough: bridge downloads, the pre-filled config and whether a bridge ever connected.</summary>
[TestFixture]
internal sealed class SetupTests
{
    private const string PublicUrl = "https://mcpal.example.com";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static Dictionary<string, string?> Settings(string? latest = "1.2.3") => new()
    {
        ["Mcpal:PublicUrl"] = PublicUrl,
        ["Mcpal:LatestBridgeVersion"] = latest,
    };

    private static async Task<PortalClient> SignedUpAsync(ServerWebApplicationFactory factory, string company = "Acme", string email = "admin@acme.example")
    {
        var portal = new PortalClient(factory);
        using var response = await portal.SignupAsync(company, email, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return portal;
    }

    private static async Task<JsonElement> GetSetupAsync(PortalClient portal)
    {
        using var response = await portal.GetAsync("/api/portal/setup", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await PortalClient.JsonAsync(response, Ct);
    }

    private static async Task<string> CreateBridgeKeyAsync(PortalClient portal)
    {
        using var created = await portal.PostAsync("/api/portal/keys", new { name = "hq", purpose = "bridge" }, Ct);
        return (await PortalClient.JsonAsync(created, Ct)).GetProperty("key").GetString() ?? string.Empty;
    }

    [Test]
    public async Task Setup_Owner_ReturnsServerUrlAndReleaseDownloads()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);

        var setup = await GetSetupAsync(portal);

        setup.GetProperty("mcpalUrl").GetString().Should().Be(PublicUrl);
        setup.GetProperty("bridgeVersion").GetString().Should().Be("1.2.3");
        setup.GetProperty("downloads").GetArrayLength().Should().Be(3);
        setup.GetProperty("downloads")[0].GetProperty("rid").GetString().Should().Be("linux-x64");
        setup.GetProperty("downloads")[0].GetProperty("url").GetString().Should().EndWith("/download/v1.2.3/mcpal-bridge-1.2.3-linux-x64.tar.gz");
        setup.GetProperty("checksumsUrl").GetString().Should().EndWith("/download/v1.2.3/sha256sums.txt");
    }

    [Test]
    public async Task Setup_Owner_ReturnsTheBridgeImageOfTheLatestVersion()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);

        (await GetSetupAsync(portal)).GetProperty("imageReference").GetString().Should().Be("ghcr.io/jabbakadabra/mcpal-bridge:1.2.3");
    }

    [Test]
    public async Task Setup_BridgeImageConfiguredAndNoVersion_UsesItWithLatestTag()
    {
        var settings = Settings(latest: null);
        settings["Mcpal:BridgeImage"] = "registry.example.com/acme/bridge";
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: settings);
        using var portal = await SignedUpAsync(factory);

        (await GetSetupAsync(portal)).GetProperty("imageReference").GetString().Should().Be("registry.example.com/acme/bridge:latest");
    }

    [Test]
    public async Task Setup_NoLatestVersion_FallsBackToLatestReleasePage()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings(latest: null));
        using var portal = await SignedUpAsync(factory);

        var setup = await GetSetupAsync(portal);

        setup.GetProperty("downloads").GetArrayLength().Should().Be(0);
        setup.GetProperty("checksumsUrl").ValueKind.Should().Be(JsonValueKind.Null);
        setup.GetProperty("releasesUrl").GetString().Should().EndWith("/releases/latest");
    }

    [Test]
    public async Task Setup_BridgeReleaseBaseUrlConfigured_IsUsedForLinks()
    {
        var settings = Settings();
        settings["Mcpal:BridgeReleaseBaseUrl"] = "https://downloads.example.com/mcpal";
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: settings);
        using var portal = await SignedUpAsync(factory);

        var setup = await GetSetupAsync(portal);

        setup.GetProperty("downloads")[0].GetProperty("url").GetString().Should().StartWith("https://downloads.example.com/mcpal/download/v1.2.3/");
    }

    [Test]
    public async Task Setup_ConfigJson_HoldsServerUrlButNeitherKeyNorServers()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);

        var setup = await GetSetupAsync(portal);

        using var config = JsonDocument.Parse(setup.GetProperty("configJson").GetString() ?? string.Empty);
        var mcpal = config.RootElement.GetProperty("mcpal");
        mcpal.GetProperty("url").GetString().Should().Be(PublicUrl);
        mcpal.TryGetProperty("apiKey", out _).Should().BeFalse();
        config.RootElement.TryGetProperty("mcpServers", out _).Should().BeFalse();
    }

    [Test]
    public async Task Setup_SampleMcpJson_IsAnEchoServerInClaudeCodeFormat()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);

        var setup = await GetSetupAsync(portal);

        using var sample = JsonDocument.Parse(setup.GetProperty("sampleMcpJson").GetString() ?? string.Empty);
        var server = sample.RootElement.GetProperty("mcpServers").GetProperty("everything");
        server.GetProperty("command").GetString().Should().Be("npx");
        server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).Should().Equal("-y", "@modelcontextprotocol/server-everything", "stdio");
        server.GetProperty("includeTools").EnumerateArray().Select(a => a.GetString()).Should().Equal("echo");
    }

    [Test]
    public async Task Setup_Member_IsForbidden()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var owner = await SignedUpAsync(factory);
        var company = await factory.SeedCompanyAsync("Globex", Ct);
        await factory.SeedUserAsync(company.CompanyId, "member@globex.example", MCPal.Server.Tenancy.PortalRole.Member, Ct);
        using var member = new PortalClient(factory);
        using var login = await member.PostAsync("/api/portal/auth/login", new { email = "member@globex.example", password = PortalClient.Password }, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await member.GetAsync("/api/portal/setup", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Setup_NotSignedIn_IsUnauthorized()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = new PortalClient(factory);

        using var response = await portal.GetAsync("/api/portal/setup", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Setup_NewCompany_HasNoConnectedBridge()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);
        await CreateBridgeKeyAsync(portal);

        (await GetSetupAsync(portal)).GetProperty("hasConnectedBridge").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task Setup_BridgeOnline_HasConnectedBridge()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);
        var rawKey = await CreateBridgeKeyAsync(portal);
        await using var bridge = await FakeBridge.StartAsync(factory, rawKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);

        (await GetSetupAsync(portal)).GetProperty("hasConnectedBridge").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task Setup_BridgeConnectedBeforeAndGone_StillHasConnectedBridge()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var portal = await SignedUpAsync(factory);
        var rawKey = await CreateBridgeKeyAsync(portal);
        var bridge = await FakeBridge.StartAsync(factory, rawKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await bridge.DisposeAsync();

        (await GetSetupAsync(portal)).GetProperty("hasConnectedBridge").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task Setup_OtherCompanyBridgeOnline_DoesNotCount()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        using var acme = await SignedUpAsync(factory);
        using var globex = await SignedUpAsync(factory, "Globex", "admin@globex.example");
        var globexKey = await CreateBridgeKeyAsync(globex);
        await using var bridge = await FakeBridge.StartAsync(factory, globexKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);

        (await GetSetupAsync(acme)).GetProperty("hasConnectedBridge").GetBoolean().Should().BeFalse();
        (await GetSetupAsync(globex)).GetProperty("hasConnectedBridge").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task Setup_PersonalKeyUsed_DoesNotCountAsBridge()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: Settings());
        var company = await factory.SeedCompanyAsync("Acme", Ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<MCPal.Server.Tenancy.IApiKeyService>().ValidateAsync(company.PersonalKey, Ct)).Should().NotBeNull();
        }

        using var portal = new PortalClient(factory);
        using var login = await portal.PostAsync("/api/portal/auth/login", new { email = company.OwnerEmail, password = PortalClient.Password }, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetSetupAsync(portal)).GetProperty("hasConnectedBridge").GetBoolean().Should().BeFalse();
    }
}
