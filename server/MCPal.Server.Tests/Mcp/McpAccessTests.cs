using MCPal.Contracts;
using MCPal.Server.Access;
using MCPal.Server.Portal;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Audit;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MCPal.Server.Tests.Mcp;

/// <summary>What each user sees and may call on /mcp depends on their groups. Owners see everything.</summary>
[TestFixture]
internal sealed class McpAccessTests
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

    private static BridgeCatalog TwoServers() => new("fake", "1.0", ProtocolVersion.Current,
    [
        new ServerCatalog("jira", [new ToolDescriptor("search", null, "Find", "{\"type\":\"object\"}", null), new ToolDescriptor("delete", null, "Remove", "{\"type\":\"object\"}", null)]),
        new ServerCatalog("hr", [new ToolDescriptor("salaries", null, "Secret", "{\"type\":\"object\"}", null)]),
    ]);

    private static async Task<T> WithAccessAsync<T>(ServerWebApplicationFactory factory, Func<AccessService, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AccessService>());
    }

    private static async Task RemoveEveryoneGrantsAsync(ServerWebApplicationFactory factory, Guid companyId)
    {
        await WithAccessAsync(factory, async access =>
        {
            var everyone = (await access.ListGroupsAsync(companyId, Ct)).Single(g => g.IsEveryone);
            foreach (var grant in everyone.Grants)
            {
                await access.DeleteGrantAsync(companyId, grant.Id, Ct);
            }

            return true;
        });
    }

    private static async Task<(Guid GroupId, Guid GrantId)> GrantAsync(ServerWebApplicationFactory factory, Guid companyId, string group, string[] userIds, string server, params string[] tools)
    {
        return await WithAccessAsync(factory, async access =>
        {
            var created = (await access.CreateGroupAsync(companyId, group, Ct)).Value ?? throw new InvalidOperationException("group");
            await access.SetMembersAsync(companyId, created.Id, userIds, Ct);
            var grant = (await access.AddGrantAsync(companyId, created.Id, server, tools, Ct)).Value ?? throw new InvalidOperationException("grant");
            return (created.Id, grant.Id);
        });
    }

    [Test]
    public async Task ListTools_NewCompany_EveryoneSeesAllTools()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, TwoServers(), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, anna.PersonalKey);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        tools.Select(t => t.Name).Should().BeEquivalentTo("jira__search", "jira__delete", "hr__salaries");
    }

    [Test]
    public async Task ListTools_GroupsDecidePerUser_OwnersSeeEverything()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        var ben = await factory.SeedUserAsync(acme.CompanyId, "ben@acme.example", PortalRole.Member, Ct);
        await RemoveEveryoneGrantsAsync(factory, acme.CompanyId);
        await GrantAsync(factory, acme.CompanyId, "hr", [anna.UserId], "hr", "*");
        await GrantAsync(factory, acme.CompanyId, "jira readers", [ben.UserId], "JIRA", "search");
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, TwoServers(), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var annaClient = await ConnectAsync(factory, anna.PersonalKey);
        await using var benClient = await ConnectAsync(factory, ben.PersonalKey);
        await using var ownerClient = await ConnectAsync(factory, acme.PersonalKey);

        var annaTools = await annaClient.ListToolsAsync(cancellationToken: Ct);
        var benTools = await benClient.ListToolsAsync(cancellationToken: Ct);
        var ownerTools = await ownerClient.ListToolsAsync(cancellationToken: Ct);

        annaTools.Select(t => t.Name).Should().Equal("hr__salaries");
        benTools.Select(t => t.Name).Should().Equal("jira__search");
        ownerTools.Select(t => t.Name).Should().BeEquivalentTo("jira__search", "jira__delete", "hr__salaries");
    }

    [Test]
    public async Task ListTools_UserWithoutAnyGrant_SeesNoTools()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        await RemoveEveryoneGrantsAsync(factory, acme.CompanyId);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, TwoServers(), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, anna.PersonalKey);

        (await client.ListToolsAsync(cancellationToken: Ct)).Should().BeEmpty();
    }

    [Test]
    public async Task CallTool_ForbiddenTool_AnswersLikeUnknownAndNeverReachesTheBridge()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        await RemoveEveryoneGrantsAsync(factory, acme.CompanyId);
        await GrantAsync(factory, acme.CompanyId, "jira", [anna.UserId], "jira", "search");
        var reached = new List<string>();
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, TwoServers(), request =>
        {
            reached.Add($"{request.ServerName}/{request.ToolName}");
            return Task.FromResult(FakeBridge.Text("data"));
        }, Ct);
        await using var client = await ConnectAsync(factory, anna.PersonalKey);

        var forbiddenTool = await client.CallToolAsync("jira__delete", cancellationToken: Ct);
        var forbiddenServer = await client.CallToolAsync("hr__salaries", cancellationToken: Ct);
        var unknown = await client.CallToolAsync("nope__nothing", cancellationToken: Ct);
        var allowed = await client.CallToolAsync("jira__search", cancellationToken: Ct);

        static string TextOf(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;
        forbiddenTool.IsError.Should().BeTrue();
        TextOf(forbiddenTool).Should().Be(TextOf(unknown).Replace("nope__nothing", "jira__delete", StringComparison.Ordinal));
        TextOf(forbiddenServer).Should().Be(TextOf(unknown).Replace("nope__nothing", "hr__salaries", StringComparison.Ordinal));
        allowed.IsError.Should().NotBe(true);
        reached.Should().Equal("jira/search");
        var rows = await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 4, Ct);
        rows.Where(r => r.PublicName is "jira__delete" or "hr__salaries" or "nope__nothing").Should().OnlyContain(r => r.Outcome == "offline");
    }

    [Test]
    public async Task Access_GrantRemoved_TakesEffectOnTheNextRequest()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        await RemoveEveryoneGrantsAsync(factory, acme.CompanyId);
        var (_, grantId) = await GrantAsync(factory, acme.CompanyId, "jira", [anna.UserId], "jira", "*");
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, TwoServers(), _ => Task.FromResult(FakeBridge.Text("data")), Ct);
        await using var client = await ConnectAsync(factory, anna.PersonalKey);
        (await client.ListToolsAsync(cancellationToken: Ct)).Should().NotBeEmpty();
        (await client.CallToolAsync("jira__search", cancellationToken: Ct)).IsError.Should().NotBe(true);

        await WithAccessAsync(factory, access => access.DeleteGrantAsync(acme.CompanyId, grantId, Ct));

        (await client.ListToolsAsync(cancellationToken: Ct)).Should().BeEmpty();
        (await client.CallToolAsync("jira__search", cancellationToken: Ct)).IsError.Should().BeTrue();
    }

    [Test]
    public async Task Access_UserAddedToGroup_SeesToolsOnTheNextRequest()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        await RemoveEveryoneGrantsAsync(factory, acme.CompanyId);
        var (groupId, _) = await GrantAsync(factory, acme.CompanyId, "hr", [], "hr", "*");
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, TwoServers(), _ => Task.FromResult(FakeBridge.Text("data")), Ct);
        await using var client = await ConnectAsync(factory, anna.PersonalKey);
        (await client.ListToolsAsync(cancellationToken: Ct)).Should().BeEmpty();

        await WithAccessAsync(factory, access => access.SetMembersAsync(acme.CompanyId, groupId, [anna.UserId], Ct));

        (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Should().Equal("hr__salaries");
    }

    [Test]
    public async Task Access_OAuthTokenOfMember_FollowsTheSameRules()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        await RemoveEveryoneGrantsAsync(factory, acme.CompanyId);
        await GrantAsync(factory, acme.CompanyId, "hr", [anna.UserId], "hr", "*");
        var token = await factory.IssueAccessTokenAsync(acme.CompanyId, anna.UserId, "claude", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, TwoServers(), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, token);

        (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Should().Equal("hr__salaries");
    }
}
