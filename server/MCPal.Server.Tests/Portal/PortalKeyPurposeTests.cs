using System.Net;
using MCPal.Server.Tests.Infrastructure;

namespace MCPal.Server.Tests.Portal;

[TestFixture]
internal sealed class PortalKeyPurposeTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static List<string> Servers(params string[] names) => [.. names];

    private static async Task<PortalClient> SignedUpAsync(ServerWebApplicationFactory factory, string company = "Acme", string email = "a@acme.example")
    {
        var portal = new PortalClient(factory);
        using var response = await portal.SignupAsync(company, email, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return portal;
    }

    [Test]
    public async Task CreateKey_ClientKeyWithServers_ReturnsAndListsPurposeAndServers()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var created = await portal.PostAsync("/api/portal/keys", new { name = "jira team", purpose = "client", allowedServers = Servers("jira", "wiki.v2") }, Ct);
        using var list = await portal.GetAsync("/api/portal/keys", Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdBody = await PortalClient.JsonAsync(created, Ct);
        createdBody.GetProperty("purpose").GetString().Should().Be("client");
        createdBody.GetProperty("allowedServers").EnumerateArray().Select(e => e.GetString()).Should().Equal("jira", "wiki.v2");
        var listed = (await PortalClient.JsonAsync(list, Ct)).EnumerateArray().Single();
        listed.GetProperty("purpose").GetString().Should().Be("client");
        listed.GetProperty("allowedServers").EnumerateArray().Select(e => e.GetString()).Should().Equal("jira", "wiki.v2");
    }

    [Test]
    public async Task CreateKey_NoPurpose_DefaultsToAnyWithoutRestrictions()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var created = await portal.PostAsync("/api/portal/keys", new { name = "legacy style" }, Ct);

        var body = await PortalClient.JsonAsync(created, Ct);
        body.GetProperty("purpose").GetString().Should().Be("any");
        body.GetProperty("allowedServers").GetArrayLength().Should().Be(0);
    }

    [Test]
    public async Task CreateKey_BridgeKey_IsCreatedWithPurposeBridge()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var created = await portal.PostAsync("/api/portal/keys", new { name = "hq bridge", purpose = "BRIDGE" }, Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await PortalClient.JsonAsync(created, Ct)).GetProperty("purpose").GetString().Should().Be("bridge");
    }

    [Test]
    public async Task CreateKey_UnknownPurpose_Returns400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var response = await portal.PostAsync("/api/portal/keys", new { name = "x", purpose = "admin" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateKey_BridgeKeyWithServers_Returns400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var response = await portal.PostAsync("/api/portal/keys", new { name = "x", purpose = "bridge", allowedServers = Servers("jira") }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("errors")[0].GetString().Should().Contain("Bridge keys");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("bad\nname")]
    public async Task CreateKey_InvalidServerName_Returns400(string server)
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var response = await portal.PostAsync("/api/portal/keys", new { name = "x", purpose = "client", allowedServers = Servers(server) }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateKey_ServerNameTooLongOrTooManyServers_Returns400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var tooLong = await portal.PostAsync("/api/portal/keys", new { name = "x", purpose = "client", allowedServers = Servers(new string('s', 201)) }, Ct);
        using var tooMany = await portal.PostAsync("/api/portal/keys", new { name = "x", purpose = "client", allowedServers = Enumerable.Range(0, 51).Select(i => $"s{i}").ToArray() }, Ct);

        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateKey_ServerThatIsNotOnline_IsAccepted()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var response = await portal.PostAsync("/api/portal/keys", new { name = "x", purpose = "client", allowedServers = Servers("not-online-yet") }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Test]
    public async Task ListKeys_TwoCompanies_EachSeesOnlyOwnRestrictions()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var acme = await SignedUpAsync(factory);
        using var globex = await SignedUpAsync(factory, "Globex", "g@globex.example");
        (await acme.PostAsync("/api/portal/keys", new { name = "acme key", purpose = "client", allowedServers = Servers("secret-acme-server") }, Ct)).Dispose();

        using var list = await globex.GetAsync("/api/portal/keys", Ct);

        (await list.Content.ReadAsStringAsync(Ct)).Should().NotContain("secret-acme-server");
    }
}
