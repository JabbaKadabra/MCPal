using System.Net;
using System.Text.Json;
using MCPal.Contracts;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;

namespace MCPal.Server.Tests.Access;

/// <summary>Groups, grants and effective access through the portal API.</summary>
[TestFixture]
internal sealed class AccessEndpointTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static readonly string[] AllTools = ["*"];
    private static readonly string[] Nobody = ["nobody"];
    private static readonly string[] ListAndGet = ["list_*", "get_?"];
    private static readonly string[] BadPattern = ["a b"];
    private static readonly string[] SearchOnly = ["search"];

    private static async Task<PortalClient> SignInAsync(ServerWebApplicationFactory factory, string email)
    {
        var portal = new PortalClient(factory);
        using var response = await portal.PostAsync("/api/portal/auth/login", new { email, password = PortalClient.Password }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return portal;
    }

    private static async Task<JsonElement> JsonOkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync(Ct));
        return await PortalClient.JsonAsync(response, Ct);
    }

    private static async Task<Guid> CreateGroupAsync(PortalClient owner, string name)
    {
        using var response = await owner.PostAsync("/api/portal/groups", new { name }, Ct);
        return (await JsonOkAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> EveryoneIdAsync(PortalClient owner)
    {
        using var response = await owner.GetAsync("/api/portal/groups", Ct);
        return (await JsonOkAsync(response)).EnumerateArray().Single(g => g.GetProperty("isEveryone").GetBoolean()).GetProperty("id").GetGuid();
    }

    [Test]
    public async Task Groups_NewCompany_ListsEveryoneWithAGrantForAllTools()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var owner = new PortalClient(factory);
        (await owner.SignupAsync("Acme", "owner@acme.example", Ct)).Dispose();

        using var response = await owner.GetAsync("/api/portal/groups", Ct);

        var groups = await JsonOkAsync(response);
        var everyone = groups.EnumerateArray().Should().ContainSingle().Which;
        everyone.GetProperty("name").GetString().Should().Be("Everyone");
        everyone.GetProperty("isEveryone").GetBoolean().Should().BeTrue();
        var grant = everyone.GetProperty("grants").EnumerateArray().Should().ContainSingle().Which;
        grant.GetProperty("serverPattern").GetString().Should().Be("*");
        grant.GetProperty("toolPatterns").EnumerateArray().Select(e => e.GetString()).Should().Equal("*");
    }

    [Test]
    public async Task Groups_Member_CannotManageGroupsGrantsOrSeeOthersAccess()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        using var owner = await SignInAsync(factory, acme.OwnerEmail);
        using var member = await SignInAsync(factory, anna.Email);
        var groupId = await CreateGroupAsync(owner, "hr");
        var everyoneId = await EveryoneIdAsync(owner);

        using var list = await member.GetAsync("/api/portal/groups", Ct);
        using var create = await member.PostAsync("/api/portal/groups", new { name = "x" }, Ct);
        using var rename = await member.PatchAsync($"/api/portal/groups/{groupId}", new { name = "y" }, Ct);
        using var delete = await member.DeleteAsync($"/api/portal/groups/{groupId}", Ct);
        using var grant = await member.PostAsync($"/api/portal/groups/{everyoneId}/grants", new { serverPattern = "*", toolPatterns = AllTools }, Ct);
        using var others = await member.GetAsync($"/api/portal/access/users/{acme.OwnerUserId}", Ct);

        foreach (var response in new[] { list, create, rename, delete, grant, others })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    [Test]
    public async Task Groups_Owner_CreatesRenamesAndDeletes()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var owner = await SignInAsync(factory, acme.OwnerEmail);

        var id = await CreateGroupAsync(owner, "  hr  ");
        using var renamed = await owner.PatchAsync($"/api/portal/groups/{id}", new { name = "human resources" }, Ct);
        using var listed = await owner.GetAsync("/api/portal/groups", Ct);
        using var deleted = await owner.DeleteAsync($"/api/portal/groups/{id}", Ct);
        using var listedAgain = await owner.GetAsync("/api/portal/groups", Ct);

        (await JsonOkAsync(renamed)).GetProperty("name").GetString().Should().Be("human resources");
        (await JsonOkAsync(listed)).EnumerateArray().Select(g => g.GetProperty("name").GetString()).Should().Equal("Everyone", "human resources");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await JsonOkAsync(listedAgain)).GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task Everyone_CannotBeRenamedDeletedOrGetMembers()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var owner = await SignInAsync(factory, acme.OwnerEmail);
        var everyoneId = await EveryoneIdAsync(owner);

        using var rename = await owner.PatchAsync($"/api/portal/groups/{everyoneId}", new { name = "All" }, Ct);
        using var delete = await owner.DeleteAsync($"/api/portal/groups/{everyoneId}", Ct);
        using var members = await owner.PutAsync($"/api/portal/groups/{everyoneId}/members", new { userIds = new[] { acme.OwnerUserId } }, Ct);

        rename.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        delete.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        members.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await EveryoneIdAsync(owner)).Should().Be(everyoneId);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("everyone")]
    [TestCase("EVERYONE")]
    public async Task Groups_EmptyOrTakenName_Returns400(string name)
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var owner = await SignInAsync(factory, acme.OwnerEmail);

        using var response = await owner.PostAsync("/api/portal/groups", new { name }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Groups_DuplicateNameIgnoringCase_Returns400ButSameNameInOtherCompanyIsFine()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var globex = await factory.SeedCompanyAsync("Globex", Ct);
        using var acmeOwner = await SignInAsync(factory, acme.OwnerEmail);
        using var globexOwner = await SignInAsync(factory, globex.OwnerEmail);
        await CreateGroupAsync(acmeOwner, "hr");

        using var duplicate = await acmeOwner.PostAsync("/api/portal/groups", new { name = "HR" }, Ct);
        using var other = await globexOwner.PostAsync("/api/portal/groups", new { name = "hr" }, Ct);

        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        other.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Test]
    public async Task Members_Set_ReplacesTheListAndRejectsUnknownOrForeignUsers()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var globex = await factory.SeedCompanyAsync("Globex", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        var ben = await factory.SeedUserAsync(acme.CompanyId, "ben@acme.example", PortalRole.Member, Ct);
        using var owner = await SignInAsync(factory, acme.OwnerEmail);
        var groupId = await CreateGroupAsync(owner, "hr");

        using var first = await owner.PutAsync($"/api/portal/groups/{groupId}/members", new { userIds = new[] { anna.UserId, ben.UserId } }, Ct);
        using var replaced = await owner.PutAsync($"/api/portal/groups/{groupId}/members", new { userIds = new[] { ben.UserId } }, Ct);
        using var unknown = await owner.PutAsync($"/api/portal/groups/{groupId}/members", new { userIds = Nobody }, Ct);
        using var foreign = await owner.PutAsync($"/api/portal/groups/{groupId}/members", new { userIds = new[] { globex.OwnerUserId } }, Ct);

        (await JsonOkAsync(first)).GetProperty("memberIds").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(anna.UserId, ben.UserId);
        (await JsonOkAsync(replaced)).GetProperty("memberIds").EnumerateArray().Select(e => e.GetString()).Should().Equal(ben.UserId);
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        foreign.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Grants_AddUpdateDelete_ChangeTheGroup()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var owner = await SignInAsync(factory, acme.OwnerEmail);
        var groupId = await CreateGroupAsync(owner, "hr");

        using var added = await owner.PostAsync($"/api/portal/groups/{groupId}/grants", new { serverPattern = "hr*", toolPatterns = ListAndGet }, Ct);
        var grantId = (await JsonOkAsync(added, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        using var updated = await owner.PutAsync($"/api/portal/grants/{grantId}", new { serverPattern = "hr", toolPatterns = AllTools }, Ct);
        using var listed = await owner.GetAsync("/api/portal/groups", Ct);
        using var deleted = await owner.DeleteAsync($"/api/portal/grants/{grantId}", Ct);
        using var deletedAgain = await owner.DeleteAsync($"/api/portal/grants/{grantId}", Ct);

        var updatedBody = await JsonOkAsync(updated);
        updatedBody.GetProperty("serverPattern").GetString().Should().Be("hr");
        updatedBody.GetProperty("toolPatterns").EnumerateArray().Select(e => e.GetString()).Should().Equal("*");
        var hr = (await JsonOkAsync(listed)).EnumerateArray().Single(g => g.GetProperty("name").GetString() == "hr");
        hr.GetProperty("grants").GetArrayLength().Should().Be(1);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        deletedAgain.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Grants_InvalidPatterns_Return400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var owner = await SignInAsync(factory, acme.OwnerEmail);
        var groupId = await CreateGroupAsync(owner, "hr");

        using var noTools = await owner.PostAsync($"/api/portal/groups/{groupId}/grants", new { serverPattern = "hr", toolPatterns = Array.Empty<string>() }, Ct);
        using var badTool = await owner.PostAsync($"/api/portal/groups/{groupId}/grants", new { serverPattern = "hr", toolPatterns = BadPattern }, Ct);
        using var noServer = await owner.PostAsync($"/api/portal/groups/{groupId}/grants", new { serverPattern = " ", toolPatterns = AllTools }, Ct);
        using var tooMany = await owner.PostAsync($"/api/portal/groups/{groupId}/grants", new { serverPattern = "hr", toolPatterns = Enumerable.Range(0, 51).Select(i => $"t{i}").ToArray() }, Ct);

        foreach (var response in new[] { noTools, badTool, noServer, tooMany })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task Groups_IdsOfOtherCompany_AreNotFoundAndNothingChanges()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var globex = await factory.SeedCompanyAsync("Globex", Ct);
        using var acmeOwner = await SignInAsync(factory, acme.OwnerEmail);
        using var globexOwner = await SignInAsync(factory, globex.OwnerEmail);
        var groupId = await CreateGroupAsync(acmeOwner, "hr");
        using var added = await acmeOwner.PostAsync($"/api/portal/groups/{groupId}/grants", new { serverPattern = "hr", toolPatterns = AllTools }, Ct);
        var grantId = (await JsonOkAsync(added, HttpStatusCode.Created)).GetProperty("id").GetGuid();

        using var rename = await globexOwner.PatchAsync($"/api/portal/groups/{groupId}", new { name = "pwned" }, Ct);
        using var delete = await globexOwner.DeleteAsync($"/api/portal/groups/{groupId}", Ct);
        using var members = await globexOwner.PutAsync($"/api/portal/groups/{groupId}/members", new { userIds = new[] { globex.OwnerUserId } }, Ct);
        using var grant = await globexOwner.PostAsync($"/api/portal/groups/{groupId}/grants", new { serverPattern = "*", toolPatterns = AllTools }, Ct);
        using var updateGrant = await globexOwner.PutAsync($"/api/portal/grants/{grantId}", new { serverPattern = "*", toolPatterns = AllTools }, Ct);
        using var deleteGrant = await globexOwner.DeleteAsync($"/api/portal/grants/{grantId}", Ct);
        using var access = await globexOwner.GetAsync($"/api/portal/access/users/{acme.OwnerUserId}", Ct);
        using var globexList = await globexOwner.GetAsync("/api/portal/groups", Ct);
        using var acmeList = await acmeOwner.GetAsync("/api/portal/groups", Ct);

        foreach (var response in new[] { rename, delete, members, grant, updateGrant, deleteGrant, access })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        (await globexList.Content.ReadAsStringAsync(Ct)).Should().NotContain("hr");
        var hr = (await JsonOkAsync(acmeList)).EnumerateArray().Single(g => g.GetProperty("name").GetString() == "hr");
        hr.GetProperty("grants").GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task AccessMe_ShowsRoleGroupsAndOnlineToolsTheUserMayUse()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        using var owner = await SignInAsync(factory, acme.OwnerEmail);
        using var member = await SignInAsync(factory, anna.Email);
        var everyoneId = await EveryoneIdAsync(owner);
        using var grants = await owner.GetAsync("/api/portal/groups", Ct);
        var everyoneGrantId = (await JsonOkAsync(grants)).EnumerateArray().Single(g => g.GetProperty("isEveryone").GetBoolean()).GetProperty("grants")[0].GetProperty("id").GetGuid();
        (await owner.DeleteAsync($"/api/portal/grants/{everyoneGrantId}", Ct)).Dispose();
        (await owner.PostAsync($"/api/portal/groups/{everyoneId}/grants", new { serverPattern = "kb", toolPatterns = SearchOnly }, Ct)).Dispose();
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search", "delete"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);

        using var mine = await member.GetAsync("/api/portal/access/me", Ct);
        using var ownerMine = await owner.GetAsync("/api/portal/access/me", Ct);
        using var viewed = await owner.GetAsync($"/api/portal/access/users/{anna.UserId}", Ct);

        var body = await JsonOkAsync(mine);
        body.GetProperty("role").GetString().Should().Be("member");
        body.GetProperty("allTools").GetBoolean().Should().BeFalse();
        body.GetProperty("groups").EnumerateArray().Select(e => e.GetString()).Should().Equal("Everyone");
        body.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("publicName").GetString()).Should().Equal("kb__search");
        var ownerBody = await JsonOkAsync(ownerMine);
        ownerBody.GetProperty("allTools").GetBoolean().Should().BeTrue();
        ownerBody.GetProperty("tools").GetArrayLength().Should().Be(2);
        (await JsonOkAsync(viewed)).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("tool").GetString()).Should().Equal("search");
    }

    [Test]
    public async Task Users_List_ShowsTheGroupsOfEachUser()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        using var owner = await SignInAsync(factory, acme.OwnerEmail);
        var groupId = await CreateGroupAsync(owner, "hr");
        (await owner.PutAsync($"/api/portal/groups/{groupId}/members", new { userIds = new[] { anna.UserId } }, Ct)).Dispose();

        using var response = await owner.GetAsync("/api/portal/users", Ct);

        var users = (await JsonOkAsync(response)).GetProperty("users").EnumerateArray().ToList();
        users.Single(u => u.GetProperty("email").GetString() == "anna@acme.example").GetProperty("groups").EnumerateArray().Select(e => e.GetString()).Should().Equal("hr");
        users.Single(u => u.GetProperty("email").GetString() == acme.OwnerEmail).GetProperty("groups").GetArrayLength().Should().Be(0);
    }

    [Test]
    public async Task Access_DisabledUser_HasNoGroupsAndNoTools()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        using var owner = await SignInAsync(factory, acme.OwnerEmail);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        (await owner.PostAsync($"/api/portal/users/{anna.UserId}/disable", null, Ct)).Dispose();

        using var viewed = await owner.GetAsync($"/api/portal/access/users/{anna.UserId}", Ct);

        var body = await JsonOkAsync(viewed);
        body.GetProperty("disabled").GetBoolean().Should().BeTrue();
        body.GetProperty("tools").GetArrayLength().Should().Be(0);
    }
}
