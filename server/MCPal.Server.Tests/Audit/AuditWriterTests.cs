using Autofac;
using MCPal.Server.Audit;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Server.Tests.Audit;

[TestFixture]
internal sealed class AuditWriterTests : ServerTestBase
{
    private static async Task<Guid> CreateCompanyAsync(ILifetimeScope scope)
    {
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        return company.Id;
    }

    [Test]
    public async Task Enqueue_WriterStarted_PersistsEveryEntry()
    {
        var scope = await GetServicesAsync();
        var companyId = await CreateCompanyAsync(scope);
        var writer = scope.Resolve<AuditWriter>();
        var now = scope.Resolve<FakeTimeProvider>().GetUtcNow();
        await writer.StartAsync(Ct);

        for (var i = 0; i < 25; i++)
        {
            writer.Enqueue(AuditTestData.Entry(companyId, now.AddSeconds(i), tool: $"t{i}"));
        }

        var rows = await AuditTestData.WaitForRowsAsync(scope.Resolve<IServiceProvider>(), companyId, 25, Ct);
        rows.Select(r => r.ToolName).Should().Equal(Enumerable.Range(0, 25).Select(i => $"t{i}"));
        writer.DroppedCount.Should().Be(0);
        await writer.StopAsync(Ct);
    }

    [Test]
    public async Task Enqueue_QueueFull_DropsNewEntriesAndCountsThem()
    {
        var scope = await GetServicesAsync(settings: new() { ["Mcpal:AuditQueueCapacity"] = "2" });
        var companyId = await CreateCompanyAsync(scope);
        var writer = scope.Resolve<AuditWriter>();
        var now = scope.Resolve<FakeTimeProvider>().GetUtcNow();

        for (var i = 0; i < 5; i++)
        {
            writer.Enqueue(AuditTestData.Entry(companyId, now.AddSeconds(i), tool: $"t{i}"));
        }

        writer.DroppedCount.Should().Be(3);
        await writer.StartAsync(Ct);
        var rows = await AuditTestData.WaitForRowsAsync(scope.Resolve<IServiceProvider>(), companyId, 2, Ct);
        rows.Select(r => r.ToolName).Should().Equal("t0", "t1");
        await writer.StopAsync(Ct);
    }

    [Test]
    public async Task StopAsync_EntriesStillQueued_AreWrittenBeforeShutdownCompletes()
    {
        var scope = await GetServicesAsync();
        var companyId = await CreateCompanyAsync(scope);
        var writer = scope.Resolve<AuditWriter>();
        var now = scope.Resolve<FakeTimeProvider>().GetUtcNow();
        await writer.StartAsync(Ct);
        for (var i = 0; i < 100; i++)
        {
            writer.Enqueue(AuditTestData.Entry(companyId, now.AddSeconds(i)));
        }

        await writer.StopAsync(Ct);

        (await AuditTestData.ReadAsync(scope.Resolve<IServiceProvider>(), companyId, Ct)).Should().HaveCount(100);
    }

    [Test]
    public async Task Enqueue_LongTexts_AreTruncatedToColumnSize()
    {
        var scope = await GetServicesAsync();
        var companyId = await CreateCompanyAsync(scope);
        var writer = scope.Resolve<AuditWriter>();
        var entry = AuditTestData.Entry(companyId, scope.Resolve<FakeTimeProvider>().GetUtcNow(), tool: new string('t', 500));
        entry.ErrorMessage = new string('e', 2000);
        await writer.StartAsync(Ct);

        writer.Enqueue(entry);

        var rows = await AuditTestData.WaitForRowsAsync(scope.Resolve<IServiceProvider>(), companyId, 1, Ct);
        rows.Single().ToolName.Should().HaveLength(200);
        rows.Single().ErrorMessage.Should().HaveLength(500);
        await writer.StopAsync(Ct);
    }
}
