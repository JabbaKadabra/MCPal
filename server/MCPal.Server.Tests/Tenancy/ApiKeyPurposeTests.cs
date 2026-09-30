using System.Net;
using System.Net.Http.Headers;
using MCPal.Server.Portal;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace MCPal.Server.Tests.Tenancy;

/// <summary>Bridge keys open tunnels only; personal access tokens and OAuth tokens work on /mcp only.</summary>
[TestFixture]
internal sealed class ApiKeyPurposeTests
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

    private static async Task<HttpStatusCode> PostMcpAsync(ServerWebApplicationFactory factory, string bearer)
    {
        using var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await http.SendAsync(request, Ct);
        return response.StatusCode;
    }

    [Test]
    public async Task Tunnel_PersonalAccessToken_IsRefused()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var connection = factory.CreateBridgeConnection(acme.PersonalKey);

        var act = async () => await connection.StartAsync(Ct);

        var error = await act.Should().ThrowAsync<HttpRequestException>();
        error.Which.Message.Should().Contain("403");
    }

    [Test]
    public async Task Tunnel_OAuthToken_IsRefused()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var token = await factory.IssueAccessTokenAsync(acme.CompanyId, acme.OwnerUserId, "claude", Ct);
        await using var connection = factory.CreateBridgeConnection(token);

        var act = async () => await connection.StartAsync(Ct);

        var error = await act.Should().ThrowAsync<HttpRequestException>();
        error.Which.Message.Should().Contain("403");
    }

    [Test]
    public async Task Tunnel_BridgeKey_IsAccepted()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);

        bridge.RegisterResult?.Accepted.Should().BeTrue();
    }

    [Test]
    public async Task Mcp_BridgeKey_IsRefusedWithForbidden()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);

        var status = await PostMcpAsync(factory, acme.BridgeKey);

        status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Mcp_PersonalAccessToken_IsAccepted()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        tools.Should().BeEmpty();
    }

    [Test]
    public async Task Mcp_OAuthToken_IsAccepted()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var token = await factory.IssueAccessTokenAsync(acme.CompanyId, acme.OwnerUserId, "claude", Ct);
        await using var client = await ConnectAsync(factory, token);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        tools.Should().BeEmpty();
    }

    [Test]
    public async Task Mcp_PersonalAccessTokenOfDisabledUser_IsUnauthorized()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var ben = await factory.SeedUserAsync(acme.CompanyId, "ben@acme.example", PortalRole.Member, Ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MCPalDbContext>().Users.Where(u => u.Id == ben.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Disabled, true), Ct);
        }

        var status = await PostMcpAsync(factory, ben.PersonalKey);

        status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Mcp_OAuthTokenOfDisabledUser_IsUnauthorized()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var ben = await factory.SeedUserAsync(acme.CompanyId, "ben@acme.example", PortalRole.Member, Ct);
        var token = await factory.IssueAccessTokenAsync(acme.CompanyId, ben.UserId, "claude", Ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MCPalDbContext>().Users.Where(u => u.Id == ben.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Disabled, true), Ct);
        }

        var status = await PostMcpAsync(factory, token);

        status.Should().Be(HttpStatusCode.Unauthorized);
    }
}
