using Autofac;
using MCPal.Server.Access;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.Tests.Access;

[TestFixture]
internal sealed class AccessEvaluatorTests : ServerTestBase
{
    private static async Task<AccessGroup> CreateGroupAsync(ILifetimeScope scope, Guid companyId, string name, string[] memberIds, params (string Server, string[] Tools)[] grants)
    {
        var access = scope.Resolve<AccessService>();
        var group = (await access.CreateGroupAsync(companyId, name, Ct)).Value ?? throw new InvalidOperationException("group not created");
        (await access.SetMembersAsync(companyId, group.Id, memberIds, Ct)).Succeeded.Should().BeTrue();
        foreach (var (server, tools) in grants)
        {
            (await access.AddGrantAsync(companyId, group.Id, server, tools, Ct)).Succeeded.Should().BeTrue();
        }

        return await scope.Resolve<MCPalDbContext>().AccessGroups.AsNoTracking().SingleAsync(g => g.Id == group.Id, Ct);
    }

    private static async Task RemoveEveryoneGrantsAsync(ILifetimeScope scope, Guid companyId)
    {
        await scope.Resolve<MCPalDbContext>().AccessGrants.Where(g => g.CompanyId == companyId).ExecuteDeleteAsync(Ct);
        scope.Resolve<AccessPolicyCache>().Invalidate(companyId);
    }

    [Test]
    public async Task GetUserPolicyAsync_NewCompany_MemberMayUseEverythingThroughEveryone()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var anna = await CreateUserAsync(scope, company.Id);

        var policy = await scope.Resolve<AccessEvaluator>().GetUserPolicyAsync(company.Id, anna.Id, Ct);

        policy.Should().NotBeNull();
        policy.Allows("hr", "salaries").Should().BeTrue();
        policy.GroupNames.Should().Equal("Everyone");
        policy.IsOwner.Should().BeFalse();
    }

    [Test]
    public async Task GetUserPolicyAsync_EveryoneGrantRemoved_MemberMayUseNothingButOwnerStillMayUseEverything()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var anna = await CreateUserAsync(scope, company.Id, "anna@acme.example");
        var boss = await CreateUserAsync(scope, company.Id, "boss@acme.example", PortalRole.Owner);
        await RemoveEveryoneGrantsAsync(scope, company.Id);
        var evaluator = scope.Resolve<AccessEvaluator>();

        var member = await evaluator.GetUserPolicyAsync(company.Id, anna.Id, Ct);
        var owner = await evaluator.GetUserPolicyAsync(company.Id, boss.Id, Ct);

        member?.Allows("hr", "salaries").Should().BeFalse();
        owner?.Allows("hr", "salaries").Should().BeTrue();
    }

    [Test]
    public async Task GetUserPolicyAsync_TwoGroups_GrantsAreUnited()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var anna = await CreateUserAsync(scope, company.Id);
        await RemoveEveryoneGrantsAsync(scope, company.Id);
        await CreateGroupAsync(scope, company.Id, "hr", [anna.Id], ("hr", ["*"]));
        await CreateGroupAsync(scope, company.Id, "wiki readers", [anna.Id], ("wiki", ["search", "get_*"]));

        var policy = await scope.Resolve<AccessEvaluator>().GetUserPolicyAsync(company.Id, anna.Id, Ct);

        policy.Should().NotBeNull();
        policy.Allows("hr", "anything").Should().BeTrue();
        policy.Allows("wiki", "search").Should().BeTrue();
        policy.Allows("wiki", "get_page").Should().BeTrue();
        policy.Allows("wiki", "delete_page").Should().BeFalse();
        policy.Allows("jira", "search").Should().BeFalse();
        policy.GroupNames.Should().BeEquivalentTo("Everyone", "hr", "wiki readers");
    }

    [Test]
    public async Task Allows_ServerPatternIgnoresCaseButToolPatternDoesNot()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var anna = await CreateUserAsync(scope, company.Id);
        await RemoveEveryoneGrantsAsync(scope, company.Id);
        await CreateGroupAsync(scope, company.Id, "hr", [anna.Id], ("HR*", ["Search"]));

        var policy = await scope.Resolve<AccessEvaluator>().GetUserPolicyAsync(company.Id, anna.Id, Ct);

        policy.Should().NotBeNull();
        policy.Allows("hr-portal", "Search").Should().BeTrue();
        policy.Allows("hr-portal", "search").Should().BeFalse();
    }

    [Test]
    public async Task GetUserPolicyAsync_UnknownDisabledOrForeignUser_ReturnsNull()
    {
        await using var scope = await GetServicesAsync();
        var companies = scope.Resolve<ICompanyService>();
        var acme = await companies.CreateAsync("Acme", Ct);
        var globex = await companies.CreateAsync("Globex", Ct);
        var disabled = await CreateUserAsync(scope, acme.Id, "off@acme.example");
        var foreign = await CreateUserAsync(scope, globex.Id, "eve@globex.example");
        await scope.Resolve<MCPalDbContext>().Users.Where(u => u.Id == disabled.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Disabled, true), Ct);
        var evaluator = scope.Resolve<AccessEvaluator>();

        (await evaluator.GetUserPolicyAsync(acme.Id, "nobody", Ct)).Should().BeNull();
        (await evaluator.GetUserPolicyAsync(acme.Id, disabled.Id, Ct)).Should().BeNull();
        (await evaluator.GetUserPolicyAsync(acme.Id, foreign.Id, Ct)).Should().BeNull();
    }

    [Test]
    public async Task GetUserPolicyAsync_GroupOfOtherCompany_GrantsNothing()
    {
        await using var scope = await GetServicesAsync();
        var companies = scope.Resolve<ICompanyService>();
        var acme = await companies.CreateAsync("Acme", Ct);
        var globex = await companies.CreateAsync("Globex", Ct);
        var anna = await CreateUserAsync(scope, acme.Id);
        await RemoveEveryoneGrantsAsync(scope, acme.Id);
        var boss = await CreateUserAsync(scope, globex.Id, "boss@globex.example");
        await CreateGroupAsync(scope, globex.Id, "everything", [boss.Id], ("*", ["*"]));

        var policy = await scope.Resolve<AccessEvaluator>().GetUserPolicyAsync(acme.Id, anna.Id, Ct);

        policy?.Allows("hr", "salaries").Should().BeFalse();
    }
}
