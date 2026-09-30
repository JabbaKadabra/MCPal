using Autofac;
using MCPal.Cloud.Tests.Infrastructure;
using MCPal.Cloud.Tenancy;

namespace MCPal.Cloud.Tests.Tenancy;

[TestFixture]
internal sealed class CompanyServiceTests : CloudTestBase
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
}
