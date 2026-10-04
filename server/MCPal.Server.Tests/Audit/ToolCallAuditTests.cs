using System.Net.Http.Headers;
using MCPal.Server.OAuth;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using MCPal.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;

namespace MCPal.Server.Tests.Audit;

[TestFixture]
internal sealed class ToolCallAuditTests
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

    [Test]
    public async Task CallTool_WithApiKey_WritesRowWithKeyBridgeServerAndTool()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.Outcome.Should().Be("ok");
        row.AuthKind.Should().Be("pat");
        row.UserId.Should().Be(acme.OwnerUserId);
        row.ApiKeyId.Should().Be(acme.PersonalKeyId);
        row.OAuthClientId.Should().BeNull();
        row.BridgeName.Should().Be("fake");
        row.ServerName.Should().Be("kb");
        row.ToolName.Should().Be("search");
        row.PublicName.Should().Be("kb__search");
        row.ErrorMessage.Should().BeNull();
        row.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        row.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task CallTool_WithOAuthToken_WritesRowWithOAuthClientAndNoKey()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var token = await factory.IssueAccessTokenAsync(acme.CompanyId, acme.OwnerUserId, "claude-client-1", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, token);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.AuthKind.Should().Be("oauth");
        row.OAuthClientId.Should().Be("claude-client-1");
        row.UserId.Should().Be(acme.OwnerUserId);
        row.ApiKeyId.Should().BeNull();
    }

    [Test]
    public async Task Call_ViaOAuth_RecordsUserId()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var anna = await factory.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        var token = await factory.IssueAccessTokenAsync(acme.CompanyId, anna.UserId, "claude-client-1", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, token);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single().UserId.Should().Be(anna.UserId);
    }

    [Test]
    public async Task Call_OfUnknownTool_StillRecordsTheUser()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        await client.CallToolAsync("nope__nothing", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.Outcome.Should().Be("offline");
        row.UserId.Should().Be(acme.OwnerUserId);
    }

    [Test]
    public async Task CallTool_BridgeReportsError_WritesToolErrorWithMessage()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search"),
            _ => Task.FromResult(new CallToolResponse(true, "[]", "backend exploded")), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.Outcome.Should().Be("tool_error");
        row.ErrorMessage.Should().Be("backend exploded");
    }

    [Test]
    public async Task CallTool_BridgeTooSlow_WritesTimeoutRow()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" });
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "slow"), async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), Ct);
            return FakeBridge.Text("late");
        }, Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        await client.CallToolAsync("kb__slow", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.Outcome.Should().Be("timeout");
        row.ErrorMessage.Should().Contain("timed out");
        row.DurationMs.Should().BeGreaterThanOrEqualTo(900);
    }

    [Test]
    public async Task CallTool_NoBridgeOnline_WritesOfflineRow()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.Outcome.Should().Be("offline");
        row.PublicName.Should().Be("kb__search");
        row.ApiKeyId.Should().Be(acme.PersonalKeyId);
    }

    [Test]
    public async Task CallTool_TwoCompanies_EachGetsOnlyOwnRows()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var globex = await factory.SeedCompanyAsync("Globex", Ct);
        await using var acmeBridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var acmeClient = await ConnectAsync(factory, acme.PersonalKey);
        await using var globexClient = await ConnectAsync(factory, globex.PersonalKey);

        await acmeClient.CallToolAsync("kb__search", cancellationToken: Ct);
        await globexClient.CallToolAsync("kb__search", cancellationToken: Ct);

        (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single().Outcome.Should().Be("ok");
        (await AuditTestData.WaitForRowsAsync(factory.Services, globex.CompanyId, 1, Ct)).Single().Outcome.Should().Be("offline");
    }

    [Test]
    public async Task CallTool_UnknownToolWithLongName_IsStoredTruncated()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        await client.CallToolAsync(new string('x', 400), cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.PublicName.Length.Should().BeLessThanOrEqualTo(64);
    }
}
