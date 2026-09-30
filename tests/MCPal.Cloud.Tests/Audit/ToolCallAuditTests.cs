using System.Net.Http.Headers;
using MCPal.Cloud.OAuth;
using MCPal.Cloud.Storage;
using MCPal.Cloud.Tenancy;
using MCPal.Cloud.Tests.Infrastructure;
using MCPal.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;

namespace MCPal.Cloud.Tests.Audit;

[TestFixture]
internal sealed class ToolCallAuditTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static async Task<McpClient> ConnectAsync(CloudWebApplicationFactory factory, string bearer)
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

    /// <summary>Stores an access token as the OAuth server would after a completed flow, bound to the key.</summary>
    private static async Task<string> IssueAccessTokenAsync(CloudWebApplicationFactory factory, SeededCompany company, string clientId)
    {
        var token = "oauth-test-token-" + Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MCPalDbContext>();
        db.OAuthTokens.Add(new OAuthToken
        {
            Hash = ApiKeyService.Hash(token),
            Kind = OAuthTokenKind.Access,
            CompanyId = company.CompanyId,
            ApiKeyId = company.ApiKeyId,
            ClientId = clientId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync(Ct);
        return token;
    }

    [Test]
    public async Task CallTool_WithApiKey_WritesRowWithKeyAgentServerAndTool()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "search"), _ => Task.FromResult(FakeAgent.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.Outcome.Should().Be("ok");
        row.AuthKind.Should().Be("apikey");
        row.ApiKeyId.Should().Be(acme.ApiKeyId);
        row.OAuthClientId.Should().BeNull();
        row.AgentName.Should().Be("fake");
        row.ServerName.Should().Be("kb");
        row.ToolName.Should().Be("search");
        row.PublicName.Should().Be("kb__search");
        row.ErrorMessage.Should().BeNull();
        row.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        row.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task CallTool_WithOAuthToken_WritesRowWithOAuthClientAndKey()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var token = await IssueAccessTokenAsync(factory, acme, "claude-client-1");
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "search"), _ => Task.FromResult(FakeAgent.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, token);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.AuthKind.Should().Be("oauth");
        row.OAuthClientId.Should().Be("claude-client-1");
        row.ApiKeyId.Should().Be(acme.ApiKeyId);
    }

    [Test]
    public async Task CallTool_AgentReportsError_WritesToolErrorWithMessage()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "search"),
            _ => Task.FromResult(new CallToolResponse(true, "[]", "backend exploded")), Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.Outcome.Should().Be("tool_error");
        row.ErrorMessage.Should().Be("backend exploded");
    }

    [Test]
    public async Task CallTool_AgentTooSlow_WritesTimeoutRow()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" });
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var agent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "slow"), async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), Ct);
            return FakeAgent.Text("late");
        }, Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__slow", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.Outcome.Should().Be("timeout");
        row.ErrorMessage.Should().Contain("timed out");
        row.DurationMs.Should().BeGreaterThanOrEqualTo(900);
    }

    [Test]
    public async Task CallTool_NoAgentOnline_WritesOfflineRow()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync("kb__search", cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.Outcome.Should().Be("offline");
        row.PublicName.Should().Be("kb__search");
        row.ApiKeyId.Should().Be(acme.ApiKeyId);
    }

    [Test]
    public async Task CallTool_TwoCompanies_EachGetsOnlyOwnRows()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var globex = await factory.SeedCompanyAsync("Globex", Ct);
        await using var acmeAgent = await FakeAgent.StartAsync(factory, acme.RawKey, FakeAgent.CatalogWith("kb", "search"), _ => Task.FromResult(FakeAgent.Text("x")), Ct);
        await using var acmeClient = await ConnectAsync(factory, acme.RawKey);
        await using var globexClient = await ConnectAsync(factory, globex.RawKey);

        await acmeClient.CallToolAsync("kb__search", cancellationToken: Ct);
        await globexClient.CallToolAsync("kb__search", cancellationToken: Ct);

        (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single().Outcome.Should().Be("ok");
        (await AuditTestData.WaitForRowsAsync(factory.Services, globex.CompanyId, 1, Ct)).Single().Outcome.Should().Be("offline");
    }

    [Test]
    public async Task CallTool_UnknownToolWithLongName_IsStoredTruncated()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var client = await ConnectAsync(factory, acme.RawKey);

        await client.CallToolAsync(new string('x', 400), cancellationToken: Ct);

        var row = (await AuditTestData.WaitForRowsAsync(factory.Services, acme.CompanyId, 1, Ct)).Single();
        row.PublicName.Length.Should().BeLessThanOrEqualTo(64);
    }
}
