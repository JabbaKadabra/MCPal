using Autofac;
using MCPal.Cloud.Storage;
using MCPal.Cloud.Tenancy;
using MCPal.Cloud.Tests.Infrastructure;
using MCPal.Cloud.Tunnel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Cloud.Tests.Tunnel;

[TestFixture]
internal sealed class TunnelSweeperTests : CloudTestBase
{
    [Test]
    public async Task CloseInactiveAsync_KeyExpiredWhileConnected_AbortsAndRemovesTunnel()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var key = await scope.Resolve<IApiKeyService>().CreateAsync(company.Id, "hq", time.GetUtcNow().AddHours(1), Ct);
        var registry = scope.Resolve<ConnectionRegistry>();
        var aborted = false;
        registry.Add(company.Id, "c1", key.Id, time.GetUtcNow(), () => aborted = true);
        time.Advance(TimeSpan.FromHours(2));

        var closed = await scope.Resolve<TunnelSweeper>().CloseInactiveAsync(Ct);

        closed.Should().Be(1);
        aborted.Should().BeTrue();
        registry.Connections(company.Id).Should().BeEmpty();
    }

    [Test]
    public async Task CloseInactiveAsync_CompanyDisabledWhileConnected_AbortsAndRemovesTunnel()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var key = await scope.Resolve<IApiKeyService>().CreateAsync(company.Id, "hq", null, Ct);
        var registry = scope.Resolve<ConnectionRegistry>();
        var aborted = false;
        registry.Add(company.Id, "c1", key.Id, time.GetUtcNow(), () => aborted = true);
        await scope.Resolve<MCPalDbContext>().Companies.Where(c => c.Id == company.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Disabled, true), Ct);

        await scope.Resolve<TunnelSweeper>().CloseInactiveAsync(Ct);

        aborted.Should().BeTrue();
        registry.Connections(company.Id).Should().BeEmpty();
    }

    [Test]
    public async Task CloseInactiveAsync_ActiveKeys_KeepsTunnelsOfAllCompanies()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();
        var companies = scope.Resolve<ICompanyService>();
        var keys = scope.Resolve<IApiKeyService>();
        var acme = await companies.CreateAsync("Acme", Ct);
        var globex = await companies.CreateAsync("Globex", Ct);
        var acmeKey = await keys.CreateAsync(acme.Id, "hq", time.GetUtcNow().AddDays(1), Ct);
        var globexKey = await keys.CreateAsync(globex.Id, "hq", null, Ct);
        var registry = scope.Resolve<ConnectionRegistry>();
        var aborted = 0;
        registry.Add(acme.Id, "c1", acmeKey.Id, time.GetUtcNow(), () => aborted++);
        registry.Add(globex.Id, "c2", globexKey.Id, time.GetUtcNow(), () => aborted++);

        var closed = await scope.Resolve<TunnelSweeper>().CloseInactiveAsync(Ct);

        closed.Should().Be(0);
        aborted.Should().Be(0);
        registry.AllConnections().Should().HaveCount(2);
    }

    [Test]
    public async Task CloseInactiveAsync_KeyOfOtherCompanyId_TreatsTunnelAsInactive()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();
        var companies = scope.Resolve<ICompanyService>();
        var acme = await companies.CreateAsync("Acme", Ct);
        var globex = await companies.CreateAsync("Globex", Ct);
        var globexKey = await scope.Resolve<IApiKeyService>().CreateAsync(globex.Id, "hq", null, Ct);
        var registry = scope.Resolve<ConnectionRegistry>();
        registry.Add(acme.Id, "c1", globexKey.Id, time.GetUtcNow(), () => { });

        var closed = await scope.Resolve<TunnelSweeper>().CloseInactiveAsync(Ct);

        closed.Should().Be(1);
    }
}
