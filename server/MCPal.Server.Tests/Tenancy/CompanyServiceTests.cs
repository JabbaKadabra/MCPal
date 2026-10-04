using Autofac;
using MCPal.Server.Access;
using MCPal.Server.Storage;
using Microsoft.EntityFrameworkCore;
using MCPal.Server.Tests.Infrastructure;
using MCPal.Server.Tenancy;

namespace MCPal.Server.Tests.Tenancy;

[TestFixture]
internal sealed class CompanyServiceTests : ServerTestBase
{
    [Test]
    public async Task CreateAsync_NameWithSpecialCharacters_BuildsSlug()
    {
        await using var scope = await GetServicesAsync();

        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme & Söhne GmbH", Ct);

        company.Slug.Should().Be("acme-s-hne-gmbh");
    }

    [Test]
    public async Task CreateAsync_SameNameTwice_ProducesUniqueSlugs()
    {
        await using var scope = await GetServicesAsync();
        var service = scope.Resolve<ICompanyService>();

        var first = await service.CreateAsync("Acme", Ct);
        var second = await service.CreateAsync("Acme", Ct);

        first.Slug.Should().Be("acme");
        second.Slug.Should().StartWith("acme-").And.NotBe(first.Slug);
    }

    [Test]
    public async Task FindAsync_AfterCreate_ReloadsFromStorage()
    {
        await using var scope = await GetServicesAsync();
        var service = scope.Resolve<ICompanyService>();
        var created = await service.CreateAsync("Acme", Ct);

        var found = await service.FindAsync(created.Id, Ct);

        found.Should().NotBeNull();
        found.Name.Should().Be("Acme");
    }

    [Test]
    public async Task CreateAsync_NewCompany_HasOneEveryoneGroupThatMayUseEverything()
    {
        await using var scope = await GetServicesAsync();
        var service = scope.Resolve<ICompanyService>();

        var company = await service.CreateAsync("Acme", Ct);
        var other = await service.CreateAsync("Globex", Ct);

        var db = scope.Resolve<MCPalDbContext>();
        var groups = await db.AccessGroups.AsNoTracking().Where(g => g.CompanyId == company.Id).ToListAsync(Ct);
        var everyone = groups.Should().ContainSingle().Which;
        everyone.IsEveryone.Should().BeTrue();
        everyone.Name.Should().Be("Everyone");
        var grant = (await db.AccessGrants.AsNoTracking().Where(g => g.CompanyId == company.Id).ToListAsync(Ct)).Should().ContainSingle().Which;
        grant.GroupId.Should().Be(everyone.Id);
        grant.ServerPattern.Should().Be("*");
        grant.ToolPatterns.Should().Equal("*");
        (await db.AccessGroups.AsNoTracking().CountAsync(g => g.CompanyId == other.Id, Ct)).Should().Be(1);
    }

    [Test]
    public async Task Database_SecondEveryoneGroupInACompany_IsRejected()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var db = scope.Resolve<MCPalDbContext>();
        db.AccessGroups.Add(new AccessGroup { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Everyone 2", IsEveryone = true, CreatedAt = DateTimeOffset.UtcNow });

        var act = async () => await db.SaveChangesAsync(Ct);

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
