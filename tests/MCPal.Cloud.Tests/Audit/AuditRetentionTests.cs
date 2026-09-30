using Autofac;
using MCPal.Cloud.Audit;
using MCPal.Cloud.Tenancy;
using MCPal.Cloud.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Cloud.Tests.Audit;

[TestFixture]
internal sealed class AuditRetentionTests : CloudTestBase
{
    [Test]
    public async Task DeleteExpiredAsync_RowsOlderThanRetention_AreDeletedNewerKept()
    {
        var scope = await GetServicesAsync(settings: new() { ["Mcpal:AuditRetentionDays"] = "30" });
        var time = scope.Resolve<FakeTimeProvider>();
        var companyId = (await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct)).Id;
        var now = time.GetUtcNow();
        await AuditTestData.InsertAsync(scope.Resolve<IServiceProvider>(), Ct,
            AuditTestData.Entry(companyId, now.AddDays(-31), tool: "old"),
            AuditTestData.Entry(companyId, now.AddDays(-45), tool: "older"),
            AuditTestData.Entry(companyId, now.AddDays(-29), tool: "recent"),
            AuditTestData.Entry(companyId, now, tool: "today"));

        var deleted = await scope.Resolve<AuditRetention>().DeleteExpiredAsync(Ct);

        deleted.Should().Be(2);
        (await AuditTestData.ReadAsync(scope.Resolve<IServiceProvider>(), companyId, Ct)).Select(r => r.ToolName).Should().Equal("recent", "today");
    }

    [Test]
    public async Task DeleteExpiredAsync_TimeAdvances_DeletesRowsThatBecameOld()
    {
        var scope = await GetServicesAsync(settings: new() { ["Mcpal:AuditRetentionDays"] = "10" });
        var time = scope.Resolve<FakeTimeProvider>();
        var companyId = (await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct)).Id;
        await AuditTestData.InsertAsync(scope.Resolve<IServiceProvider>(), Ct, AuditTestData.Entry(companyId, time.GetUtcNow(), tool: "today"));
        var retention = scope.Resolve<AuditRetention>();

        var before = await retention.DeleteExpiredAsync(Ct);
        time.Advance(TimeSpan.FromDays(11));
        var after = await retention.DeleteExpiredAsync(Ct);

        before.Should().Be(0);
        after.Should().Be(1);
    }

    [Test]
    public async Task DeleteExpiredAsync_MoreRowsThanOneBatch_DeletesAllOfThem()
    {
        var scope = await GetServicesAsync(settings: new() { ["Mcpal:AuditRetentionDays"] = "1" });
        var time = scope.Resolve<FakeTimeProvider>();
        var companyId = (await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct)).Id;
        var old = time.GetUtcNow().AddDays(-5);
        await AuditTestData.InsertAsync(scope.Resolve<IServiceProvider>(), Ct, [.. Enumerable.Range(0, AuditRetention.BatchSize + 10).Select(i => AuditTestData.Entry(companyId, old.AddSeconds(i)))]);

        var deleted = await scope.Resolve<AuditRetention>().DeleteExpiredAsync(Ct);

        deleted.Should().Be(AuditRetention.BatchSize + 10);
    }
}
