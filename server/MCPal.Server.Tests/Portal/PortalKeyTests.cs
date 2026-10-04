using System.Net;
using MCPal.Server.Tests.Infrastructure;

namespace MCPal.Server.Tests.Portal;

/// <summary>Personal access tokens and bridge keys through the portal API.</summary>
[TestFixture]
internal sealed class PortalKeyTests
{
    private static readonly string[] JiraOnly = ["jira"];

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static async Task<PortalClient> SignedUpAsync(ServerWebApplicationFactory factory, string company = "Acme", string email = "a@acme.example")
    {
        var portal = new PortalClient(factory);
        using var response = await portal.SignupAsync(company, email, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return portal;
    }

    [Test]
    public async Task CreateKey_NoPurpose_IsPersonalAccessTokenOfTheCaller()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var created = await portal.PostAsync("/api/portal/keys", new { name = "my laptop" }, Ct);
        using var list = await portal.GetAsync("/api/portal/keys", Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await PortalClient.JsonAsync(created, Ct)).GetProperty("purpose").GetString().Should().Be("personal");
        var listed = (await PortalClient.JsonAsync(list, Ct)).EnumerateArray().Single();
        listed.GetProperty("purpose").GetString().Should().Be("personal");
        listed.GetProperty("userEmail").GetString().Should().Be("a@acme.example");
        listed.TryGetProperty("allowedServers", out _).Should().BeFalse();
    }

    [Test]
    public async Task CreateKey_BridgeKeyByOwner_HasNoUser()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var created = await portal.PostAsync("/api/portal/keys", new { name = "hq bridge", purpose = "BRIDGE" }, Ct);
        using var list = await portal.GetAsync("/api/portal/keys", Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await PortalClient.JsonAsync(created, Ct)).GetProperty("purpose").GetString().Should().Be("bridge");
        var listed = (await PortalClient.JsonAsync(list, Ct)).EnumerateArray().Single();
        listed.GetProperty("userEmail").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        listed.GetProperty("createdBy").GetString().Should().Be("a@acme.example");
    }

    [TestCase("admin")]
    [TestCase("any")]
    [TestCase("client")]
    [TestCase("1")]
    public async Task CreateKey_UnknownOrRemovedPurpose_Returns400(string purpose)
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var response = await portal.PostAsync("/api/portal/keys", new { name = "x", purpose }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateKey_WithAllowedServers_Returns400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var portal = await SignedUpAsync(factory);

        using var response = await portal.PostAsync("/api/portal/keys", new { name = "x", allowedServers = JiraOnly }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("errors")[0].GetString().Should().Contain("access groups");
    }

    [Test]
    public async Task ListKeys_TwoCompanies_EachSeesOnlyOwnKeys()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var acme = await SignedUpAsync(factory);
        using var globex = await SignedUpAsync(factory, "Globex", "g@globex.example");
        (await acme.PostAsync("/api/portal/keys", new { name = "acme secret key" }, Ct)).Dispose();

        using var list = await globex.GetAsync("/api/portal/keys", Ct);

        (await list.Content.ReadAsStringAsync(Ct)).Should().NotContain("acme secret key");
    }
}
