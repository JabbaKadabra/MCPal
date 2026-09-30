using System.Net;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Server.Tests.Portal;

/// <summary>Several users per company: roles, invitations and their limits.</summary>
[TestFixture]
internal sealed class TeamTests
{
    private const string MemberPassword = "member-password-123";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static async Task<(PortalClient Client, Guid CompanyId)> SignupAsync(ServerWebApplicationFactory factory, string company, string email)
    {
        var portal = new PortalClient(factory);
        using var response = await portal.SignupAsync(company, email, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (portal, (await PortalClient.JsonAsync(response, Ct)).GetProperty("companyId").GetGuid());
    }

    private static async Task<string> InviteAsync(ServerWebApplicationFactory factory, PortalClient owner, string email, string role = "member")
    {
        using var response = await owner.PostAsync("/api/portal/users", new { email, role }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return factory.Emails.To(email)[^1].QueryValue("token");
    }

    private static async Task<HttpResponseMessage> AcceptAsync(PortalClient client, string token, string password = MemberPassword) =>
        await client.PostAsync("/api/portal/invitations/accept", new { token, password }, Ct);

    /// <summary>Invites and accepts; the returned client is signed in as the new user.</summary>
    private static async Task<PortalClient> JoinAsync(ServerWebApplicationFactory factory, PortalClient owner, string email, string role = "member")
    {
        var token = await InviteAsync(factory, owner, email, role);
        var member = new PortalClient(factory);
        using var accepted = await AcceptAsync(member, token);
        accepted.StatusCode.Should().Be(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync(Ct));
        return member;
    }

    private static async Task<System.Text.Json.JsonElement> GetJsonAsync(PortalClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await PortalClient.JsonAsync(response, Ct);
    }

    private static async Task<Guid> UserIdAsync(PortalClient client, string email)
    {
        var list = await GetJsonAsync(client, "/api/portal/users");
        return list.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("email").GetString() == email).GetProperty("id").GetGuid();
    }

    [Test]
    public async Task Signup_FirstUser_IsOwner()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;

        (await GetJsonAsync(owner, "/api/portal/auth/me")).GetProperty("role").GetString().Should().Be("owner");
    }

    [Test]
    public async Task Invite_ByOwner_SendsMailWithLinkAndListsPendingInvitation()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:PublicUrl"] = "https://mcpal.example.com" });
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;

        using var response = await owner.PostAsync("/api/portal/users", new { email = "New.Colleague@acme.example", role = "member" }, Ct);
        var list = await GetJsonAsync(owner, "/api/portal/users");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var mail = factory.Emails.To("New.Colleague@acme.example").Should().ContainSingle().Which;
        mail.Link().GetLeftPart(UriPartial.Path).Should().Be("https://mcpal.example.com/accept-invitation");
        mail.Subject.Should().Contain("Acme");
        var invitation = list.GetProperty("invitations").EnumerateArray().Should().ContainSingle().Which;
        invitation.GetProperty("email").GetString().Should().Be("New.Colleague@acme.example");
        invitation.GetProperty("role").GetString().Should().Be("member");
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(mail.QueryValue("token"), "the token only travels by mail");
    }

    [TestCase("not an email", "member")]
    [TestCase("x@acme.example", "admin")]
    [TestCase("", "member")]
    public async Task Invite_InvalidEmailOrRole_Returns400(string email, string role)
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;

        using var response = await owner.PostAsync("/api/portal/users", new { email, role }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Invite_AddressWithExistingAccountInAnyCompany_Returns400WithSameMessage()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (acme, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        var (globex, _) = await SignupAsync(factory, "Globex", "boss@globex.example");
        using var _ = acme;
        using var __ = globex;

        using var sameCompany = await acme.PostAsync("/api/portal/users", new { email = "owner@acme.example", role = "member" }, Ct);
        using var otherCompany = await acme.PostAsync("/api/portal/users", new { email = "boss@globex.example", role = "member" }, Ct);

        sameCompany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        otherCompany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sameCompany.Content.ReadAsStringAsync(Ct)).Should().Be(await otherCompany.Content.ReadAsStringAsync(Ct));
        factory.Emails.To("boss@globex.example").Should().NotContain(m => m.Subject.Contains("invited", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task Invite_SameAddressTwice_ReplacesTheFirstInvitation()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        var first = await InviteAsync(factory, owner, "new@acme.example");
        var second = await InviteAsync(factory, owner, "new@acme.example");
        using var newcomer = new PortalClient(factory);

        using var withFirst = await AcceptAsync(newcomer, first);
        using var withSecond = await AcceptAsync(newcomer, second);
        var list = await GetJsonAsync(owner, "/api/portal/users");

        withFirst.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        withSecond.StatusCode.Should().Be(HttpStatusCode.Created);
        list.GetProperty("invitations").GetArrayLength().Should().Be(0);
    }

    [Test]
    public async Task Accept_ValidToken_CreatesMemberInTheInvitingCompanyAndSignsIn()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, companyId) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        var token = await InviteAsync(factory, owner, "new@acme.example");
        using var member = new PortalClient(factory);

        using var accepted = await AcceptAsync(member, token);
        var me = await GetJsonAsync(member, "/api/portal/auth/me");
        var list = await GetJsonAsync(owner, "/api/portal/users");

        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        me.GetProperty("companyId").GetGuid().Should().Be(companyId);
        me.GetProperty("role").GetString().Should().Be("member");
        me.GetProperty("email").GetString().Should().Be("new@acme.example");
        list.GetProperty("users").GetArrayLength().Should().Be(2);
        list.GetProperty("invitations").GetArrayLength().Should().Be(0);
    }

    [Test]
    public async Task Accept_NewUser_CanSignInAgainWithoutConfirmingBecauseTheInvitationProvedTheMailbox()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        using var member = await JoinAsync(factory, owner, "new@acme.example");
        (await member.PostAsync("/api/portal/auth/logout", null, Ct)).Dispose();

        using var login = await member.PostAsync("/api/portal/auth/login", new { email = "new@acme.example", password = MemberPassword }, Ct);

        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Accept_OwnerRole_CreatesSecondOwner()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        using var second = await JoinAsync(factory, owner, "second@acme.example", "owner");

        (await GetJsonAsync(second, "/api/portal/auth/me")).GetProperty("role").GetString().Should().Be("owner");
    }

    [Test]
    public async Task Accept_WeakPassword_Returns400AndKeepsTheInvitationUsable()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        var token = await InviteAsync(factory, owner, "new@acme.example");
        using var member = new PortalClient(factory);

        using var weak = await AcceptAsync(member, token, "short");
        using var retry = await AcceptAsync(member, token);

        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PortalClient.JsonAsync(weak, Ct)).GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Test]
    public async Task Accept_TokenUsedTwiceUnknownOrExpired_Returns400WithSameMessage()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, timeProvider: time);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        var used = await InviteAsync(factory, owner, "used@acme.example");
        var expiring = await InviteAsync(factory, owner, "late@acme.example");
        using var first = new PortalClient(factory);
        (await AcceptAsync(first, used)).Dispose();
        time.Advance(TimeSpan.FromDays(8));
        using var browser = new PortalClient(factory);

        using var again = await AcceptAsync(browser, used);
        using var unknown = await AcceptAsync(browser, "does-not-exist");
        using var expired = await AcceptAsync(browser, expiring);

        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        expired.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var message = await unknown.Content.ReadAsStringAsync(Ct);
        (await again.Content.ReadAsStringAsync(Ct)).Should().Be(message);
        (await expired.Content.ReadAsStringAsync(Ct)).Should().Be(message);
    }

    [Test]
    public async Task Accept_TokenOfOtherCompanyWhileSignedInElsewhere_JoinsOnlyTheInvitingCompany()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (acme, acmeId) = await SignupAsync(factory, "Acme", "owner@acme.example");
        var (globex, globexId) = await SignupAsync(factory, "Globex", "boss@globex.example");
        using var _ = acme;
        using var __ = globex;
        using var acmeSecondOwner = await JoinAsync(factory, acme, "second@acme.example", "owner");
        var globexToken = await InviteAsync(factory, globex, "new@globex.example");

        // Signed in as an owner of Acme, the browser presents the invitation token of Globex.
        using var accepted = await AcceptAsync(acme, globexToken);
        var acmeUsers = await GetJsonAsync(acmeSecondOwner, "/api/portal/users");
        var globexUsers = await GetJsonAsync(globex, "/api/portal/users");

        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        (await PortalClient.JsonAsync(accepted, Ct)).GetProperty("companyId").GetGuid().Should().Be(globexId).And.NotBe(acmeId);
        acmeUsers.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()).Should().BeEquivalentTo("owner@acme.example", "second@acme.example");
        globexUsers.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()).Should().BeEquivalentTo("boss@globex.example", "new@globex.example");
    }

    [Test]
    public async Task Preview_ValidToken_ShowsCompanyAndAddressAndInvalidTokenDoesNot()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme GmbH", "owner@acme.example");
        using var _ = owner;
        var token = await InviteAsync(factory, owner, "new@acme.example", "owner");
        using var browser = new PortalClient(factory);

        var preview = await GetJsonAsync(browser, "/api/portal/invitations/preview?token=" + Uri.EscapeDataString(token));
        using var invalid = await browser.GetAsync("/api/portal/invitations/preview?token=nope", Ct);

        preview.GetProperty("companyName").GetString().Should().Be("Acme GmbH");
        preview.GetProperty("email").GetString().Should().Be("new@acme.example");
        preview.GetProperty("role").GetString().Should().Be("owner");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Accept_WithoutAntiforgeryHeader_Returns400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var browser = new PortalClient(factory);

        using var response = await browser.PostAsync("/api/portal/invitations/accept", new { token = "x", password = MemberPassword }, Ct, withCsrf: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Users_NotSignedIn_Return401()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var browser = new PortalClient(factory);

        using var list = await browser.GetAsync("/api/portal/users", Ct);
        using var invite = await browser.PostAsync("/api/portal/users", new { email = "x@acme.example", role = "member" }, Ct);

        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        invite.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Members_CannotListInviteRemoveOrCancel()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        using var member = await JoinAsync(factory, owner, "member@acme.example");
        var ownerId = await UserIdAsync(owner, "owner@acme.example");
        await InviteAsync(factory, owner, "pending@acme.example");
        var invitationId = (await GetJsonAsync(owner, "/api/portal/users")).GetProperty("invitations")[0].GetProperty("id").GetGuid();

        using var list = await member.GetAsync("/api/portal/users", Ct);
        using var invite = await member.PostAsync("/api/portal/users", new { email = "x@acme.example", role = "member" }, Ct);
        using var remove = await member.DeleteAsync($"/api/portal/users/{ownerId}", Ct);
        using var cancel = await member.DeleteAsync($"/api/portal/invitations/{invitationId}", Ct);

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        invite.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        remove.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        cancel.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Members_CanViewConnectionsConnectInfoAndAudit()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        using var member = await JoinAsync(factory, owner, "member@acme.example");

        foreach (var url in new[] { "/api/portal/connections", "/api/portal/connect-info", "/api/portal/audit" })
        {
            using var response = await member.GetAsync(url, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        }
    }

    [Test]
    public async Task Keys_Member_CreatesOnlyClientKeysAndSeesAndRevokesOnlyOwnKeys()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        using var member = await JoinAsync(factory, owner, "member@acme.example");
        using var ownerKey = await owner.PostAsync("/api/portal/keys", new { name = "bridge key", purpose = "bridge" }, Ct);
        var ownerKeyId = (await PortalClient.JsonAsync(ownerKey, Ct)).GetProperty("id").GetGuid();

        using var bridgeKey = await member.PostAsync("/api/portal/keys", new { name = "sneaky", purpose = "bridge" }, Ct);
        using var anyKey = await member.PostAsync("/api/portal/keys", new { name = "sneaky", purpose = "any" }, Ct);
        using var noPurpose = await member.PostAsync("/api/portal/keys", new { name = "sneaky" }, Ct);
        using var clientKey = await member.PostAsync("/api/portal/keys", new { name = "mine", purpose = "client" }, Ct);
        var memberKeys = await GetJsonAsync(member, "/api/portal/keys");
        var ownerKeys = await GetJsonAsync(owner, "/api/portal/keys");
        using var revokeForeign = await member.DeleteAsync($"/api/portal/keys/{ownerKeyId}", Ct);
        var mineId = (await PortalClient.JsonAsync(clientKey, Ct)).GetProperty("id").GetGuid();
        using var revokeOwn = await member.DeleteAsync($"/api/portal/keys/{mineId}", Ct);

        bridgeKey.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        anyKey.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        noPurpose.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        clientKey.StatusCode.Should().Be(HttpStatusCode.Created);
        memberKeys.EnumerateArray().Select(k => k.GetProperty("name").GetString()).Should().Equal("mine");
        ownerKeys.EnumerateArray().Select(k => k.GetProperty("name").GetString()).Should().BeEquivalentTo("bridge key", "mine");
        ownerKeys.EnumerateArray().Single(k => k.GetProperty("name").GetString() == "mine").GetProperty("createdBy").GetString().Should().Be("member@acme.example");
        revokeForeign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        revokeOwn.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task RemoveUser_Member_LosesAccessAndTheirClaudeKeysAreRevoked()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        using var member = await JoinAsync(factory, owner, "member@acme.example");
        using var keyResponse = await member.PostAsync("/api/portal/keys", new { name = "mine", purpose = "client" }, Ct);
        var memberKey = (await PortalClient.JsonAsync(keyResponse, Ct)).GetProperty("key").GetString() ?? string.Empty;
        var memberId = await UserIdAsync(owner, "member@acme.example");

        using var removed = await owner.DeleteAsync($"/api/portal/users/{memberId}", Ct);
        using var me = await member.GetAsync("/api/portal/auth/me", Ct);
        using var mcp = await BearerStatusAsync(factory, memberKey);
        var list = await GetJsonAsync(owner, "/api/portal/users");

        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mcp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        list.GetProperty("users").GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task RemoveUser_LastOwner_Returns400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        var ownerId = await UserIdAsync(owner, "owner@acme.example");

        using var response = await owner.DeleteAsync($"/api/portal/users/{ownerId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("errors")[0].GetString().Should().Contain("last owner");
        (await GetJsonAsync(owner, "/api/portal/users")).GetProperty("users").GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task RemoveUser_OwnerWhenAnotherOwnerExists_IsAllowed()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (owner, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        using var _ = owner;
        using var second = await JoinAsync(factory, owner, "second@acme.example", "owner");
        var ownerId = await UserIdAsync(second, "owner@acme.example");

        using var response = await second.DeleteAsync($"/api/portal/users/{ownerId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task RemoveUser_UserOfOtherCompanyOrUnknown_Returns404AndChangesNothing()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (acme, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        var (globex, _) = await SignupAsync(factory, "Globex", "boss@globex.example");
        using var _ = acme;
        using var __ = globex;
        var globexUser = await UserIdAsync(globex, "boss@globex.example");

        using var foreign = await acme.DeleteAsync($"/api/portal/users/{globexUser}", Ct);
        using var unknown = await acme.DeleteAsync($"/api/portal/users/{Guid.NewGuid()}", Ct);

        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetJsonAsync(globex, "/api/portal/users")).GetProperty("users").GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task CancelInvitation_Pending_MakesTokenUselessAndForeignIdReturns404()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (acme, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        var (globex, _) = await SignupAsync(factory, "Globex", "boss@globex.example");
        using var _ = acme;
        using var __ = globex;
        var token = await InviteAsync(factory, acme, "new@acme.example");
        var invitationId = (await GetJsonAsync(acme, "/api/portal/users")).GetProperty("invitations")[0].GetProperty("id").GetGuid();
        using var newcomer = new PortalClient(factory);

        using var foreign = await globex.DeleteAsync($"/api/portal/invitations/{invitationId}", Ct);
        using var cancelled = await acme.DeleteAsync($"/api/portal/invitations/{invitationId}", Ct);
        using var accept = await AcceptAsync(newcomer, token);

        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        cancelled.StatusCode.Should().Be(HttpStatusCode.NoContent);
        accept.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Users_TwoCompanies_EachListsOnlyOwnUsersAndInvitations()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var (acme, _) = await SignupAsync(factory, "Acme", "owner@acme.example");
        var (globex, _) = await SignupAsync(factory, "Globex", "boss@globex.example");
        using var _ = acme;
        using var __ = globex;
        await InviteAsync(factory, acme, "secret-colleague@acme.example");

        var globexList = await GetJsonAsync(globex, "/api/portal/users");

        globexList.GetRawText().Should().NotContain("acme.example");
    }

    private static async Task<HttpResponseMessage> BearerStatusAsync(ServerWebApplicationFactory factory, string key)
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        return await http.SendAsync(request, Ct);
    }
}
