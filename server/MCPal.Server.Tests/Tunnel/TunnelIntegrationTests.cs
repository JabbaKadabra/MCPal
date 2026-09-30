using System.Net;
using System.Net.Http.Headers;
using Autofac;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using MCPal.Server.Tunnel;
using MCPal.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace MCPal.Server.Tests.Tunnel;

[TestFixture]
internal sealed class TunnelIntegrationTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static BridgeCatalog Catalog(string server = "kb", string protocol = ProtocolVersion.Current) =>
        new("bridge-1", "1.0", protocol, [new ServerCatalog(server, [new ToolDescriptor("search", null, "Find", "{\"type\":\"object\"}", null)])]);

    [Test]
    public async Task Negotiate_WithoutCredential_Returns401WithResourceMetadataChallenge()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(new Uri("/hub/bridge/negotiate?negotiateVersion=1", UriKind.Relative), null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Bearer");
        response.Headers.WwwAuthenticate.ToString().Should().Contain("resource_metadata=\"http://localhost:8080/.well-known/oauth-protected-resource/mcp\"");
    }

    [Test]
    public async Task Negotiate_WithUnknownApiKey_Returns401()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hub/bridge/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "mcpal_00000000_" + new string('x', 40));

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Negotiate_WithApiKeyInQueryString_Returns401()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var seeded = await factory.SeedCompanyAsync("Acme", Ct);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(new Uri($"/hub/bridge/negotiate?negotiateVersion=1&access_token={seeded.RawKey}", UriKind.Relative), null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Register_ValidCatalog_ExposesToolsForCompanyOnly()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var other = await factory.SeedCompanyAsync("Other", Ct);
        await using var connection = factory.CreateBridgeConnection(acme.RawKey);
        await connection.StartAsync(Ct);

        var result = await connection.InvokeAsync<RegisterResult>("Register", Catalog(), Ct);

        result.Accepted.Should().BeTrue();
        var registry = factory.Services.GetRequiredService<ConnectionRegistry>();
        registry.Tools(acme.CompanyId).Select(t => t.PublicName).Should().Equal("kb__search");
        registry.Tools(other.CompanyId).Should().BeEmpty();
    }

    [Test]
    public async Task Register_UnsupportedProtocolMajor_IsRejectedWithMessage()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var connection = factory.CreateBridgeConnection(acme.RawKey);
        await connection.StartAsync(Ct);

        var result = await connection.InvokeAsync<RegisterResult>("Register", Catalog(protocol: "99.0"), Ct);

        result.Accepted.Should().BeFalse();
        result.Code.Should().Be("unsupported_protocol");
        result.Message.Should().Contain("99.0");
        factory.Services.GetRequiredService<ConnectionRegistry>().Tools(acme.CompanyId).Should().BeEmpty();
    }

    [Test]
    public async Task Register_ServerNameUsedByOtherConnectionOfSameCompany_IsRejected()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var first = factory.CreateBridgeConnection(acme.RawKey);
        await using var second = factory.CreateBridgeConnection(acme.RawKey);
        await first.StartAsync(Ct);
        await second.StartAsync(Ct);
        await first.InvokeAsync<RegisterResult>("Register", Catalog(), Ct);

        var result = await second.InvokeAsync<RegisterResult>("Register", Catalog(), Ct);

        result.RejectedServers.Should().ContainSingle().Which.ServerName.Should().Be("kb");
    }

    [Test]
    public async Task Disconnect_AfterRegister_RemovesConnectionFromRegistry()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var registry = factory.Services.GetRequiredService<ConnectionRegistry>();
        await using var connection = factory.CreateBridgeConnection(acme.RawKey);
        await connection.StartAsync(Ct);
        await connection.InvokeAsync<RegisterResult>("Register", Catalog(), Ct);

        await connection.StopAsync(Ct);

        await WaitUntilAsync(() => registry.Connections(acme.CompanyId).Count == 0);
        registry.Tools(acme.CompanyId).Should().BeEmpty();
    }

    [Test]
    public async Task RevokeKey_WithLiveTunnel_ClosesTunnelAndRemovesTools()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var registry = factory.Services.GetRequiredService<ConnectionRegistry>();
        await using var connection = factory.CreateBridgeConnection(acme.RawKey);
        var closed = new TaskCompletionSource();
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await connection.StartAsync(Ct);
        await connection.InvokeAsync<RegisterResult>("Register", Catalog(), Ct);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IApiKeyService>().RevokeAsync(acme.CompanyId, acme.ApiKeyId, Ct);
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        registry.Tools(acme.CompanyId).Should().BeEmpty();
    }

    [Test]
    public async Task Connect_KeyRevokedBeforeTunnelIsRegistered_ClosesTunnel()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(
            Ct,
            configureContainer: container => container.RegisterDecorator<KeyRevokedAfterAuthentication, IApiKeyService>());
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var registry = factory.Services.GetRequiredService<ConnectionRegistry>();
        await using var connection = factory.CreateBridgeConnection(acme.RawKey);
        var closed = new TaskCompletionSource();
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await connection.StartAsync(Ct);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        registry.Connections(acme.CompanyId).Should().BeEmpty();
    }

    /// <summary>Authenticates the key, then reports it inactive: the state after a revoke that lands during the handshake.</summary>
    private sealed class KeyRevokedAfterAuthentication(IApiKeyService inner) : IApiKeyService
    {
        public Task<CreatedApiKey> CreateAsync(Guid companyId, NewApiKey request, CancellationToken cancellationToken) =>
            inner.CreateAsync(companyId, request, cancellationToken);

        public Task<ValidatedKey?> ValidateAsync(string rawKey, CancellationToken cancellationToken) => inner.ValidateAsync(rawKey, cancellationToken);

        public Task<IReadOnlyList<ApiKey>> ListAsync(Guid companyId, CancellationToken cancellationToken) => inner.ListAsync(companyId, cancellationToken);

        public Task<bool> RevokeAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken) => inner.RevokeAsync(companyId, apiKeyId, cancellationToken);

        public Task<bool> IsActiveAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<ValidatedKey>> ActiveKeysAsync(IReadOnlyCollection<Guid> apiKeyIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ValidatedKey>>([]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
