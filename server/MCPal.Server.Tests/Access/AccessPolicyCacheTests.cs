using Autofac;
using MCPal.Server.Access;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Server.Tests.Access;

[TestFixture]
internal sealed class AccessPolicyCacheTests : ServerTestBase
{
    private static CompanyPolicy PolicyOf(Guid companyId) => new(companyId, new Dictionary<string, UserPolicy>());

    [Test]
    public async Task GetAsync_SecondCall_ServesTheCachedPolicy()
    {
        await using var scope = await GetServicesAsync();
        var cache = scope.Resolve<AccessPolicyCache>();
        var company = Guid.NewGuid();
        var loads = 0;
        Task<CompanyPolicy> Load(CancellationToken _) { loads++; return Task.FromResult(PolicyOf(company)); }

        await cache.GetAsync(company, Load, Ct);
        await cache.GetAsync(company, Load, Ct);

        loads.Should().Be(1);
    }

    [Test]
    public async Task GetAsync_AfterInvalidate_LoadsAgain()
    {
        await using var scope = await GetServicesAsync();
        var cache = scope.Resolve<AccessPolicyCache>();
        var company = Guid.NewGuid();
        var loads = 0;
        Task<CompanyPolicy> Load(CancellationToken _) { loads++; return Task.FromResult(PolicyOf(company)); }
        await cache.GetAsync(company, Load, Ct);

        cache.Invalidate(company);
        await cache.GetAsync(company, Load, Ct);

        loads.Should().Be(2);
    }

    [Test]
    public async Task GetAsync_InvalidateOfOtherCompany_KeepsTheEntry()
    {
        await using var scope = await GetServicesAsync();
        var cache = scope.Resolve<AccessPolicyCache>();
        var company = Guid.NewGuid();
        var loads = 0;
        Task<CompanyPolicy> Load(CancellationToken _) { loads++; return Task.FromResult(PolicyOf(company)); }
        await cache.GetAsync(company, Load, Ct);

        cache.Invalidate(Guid.NewGuid());
        await cache.GetAsync(company, Load, Ct);

        loads.Should().Be(1);
    }

    [Test]
    public async Task GetAsync_InvalidateWhileLoading_DoesNotStoreThePossiblyStaleResult()
    {
        await using var scope = await GetServicesAsync();
        var cache = scope.Resolve<AccessPolicyCache>();
        var company = Guid.NewGuid();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var loads = 0;
        async Task<CompanyPolicy> SlowLoad(CancellationToken _)
        {
            loads++;
            started.SetResult();
            await release.Task;
            return PolicyOf(company);
        }

        var pending = cache.GetAsync(company, SlowLoad, Ct);
        await started.Task;
        cache.Invalidate(company);
        release.SetResult();
        await pending;
        await cache.GetAsync(company, _ => { loads++; return Task.FromResult(PolicyOf(company)); }, Ct);

        loads.Should().Be(2);
    }

    [Test]
    public async Task GetAsync_AfterLifetime_LoadsAgain()
    {
        await using var scope = await GetServicesAsync();
        var cache = scope.Resolve<AccessPolicyCache>();
        var time = scope.Resolve<FakeTimeProvider>();
        var company = Guid.NewGuid();
        var loads = 0;
        Task<CompanyPolicy> Load(CancellationToken _) { loads++; return Task.FromResult(PolicyOf(company)); }
        await cache.GetAsync(company, Load, Ct);

        time.Advance(AccessPolicyCache.Lifetime - TimeSpan.FromSeconds(1));
        await cache.GetAsync(company, Load, Ct);
        time.Advance(TimeSpan.FromSeconds(2));
        await cache.GetAsync(company, Load, Ct);

        loads.Should().Be(2);
    }
}
