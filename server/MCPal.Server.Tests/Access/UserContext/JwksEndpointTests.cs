using System.Net;
using System.Text.Json;
using MCPal.Server.Tests.Infrastructure;

namespace MCPal.Server.Tests.Access.UserContext;

[TestFixture]
internal sealed class JwksEndpointTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task Get_Anonymous_ReturnsPublicKeysWithCacheHeader()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/jwks.json", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response.Headers.CacheControl?.ToString().Should().Be("public, max-age=300");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var key = json.RootElement.GetProperty("keys").EnumerateArray().Should().ContainSingle().Which;
        key.GetProperty("kty").GetString().Should().Be("EC");
        key.GetProperty("crv").GetString().Should().Be("P-256");
        key.GetProperty("alg").GetString().Should().Be("ES256");
        key.GetProperty("use").GetString().Should().Be("sig");
        key.GetProperty("kid").GetString().Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Get_Jwks_NeverContainsPrivateMembers()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/jwks.json", Ct);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        foreach (var key in json.RootElement.GetProperty("keys").EnumerateArray())
        {
            key.TryGetProperty("d", out _).Should().BeFalse();
        }
    }

    [Test]
    public async Task Get_SameKeyTwice_ReturnsTheSameKid()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var client = factory.CreateClient();

        var first = await client.GetStringAsync("/.well-known/jwks.json", Ct);
        var second = await client.GetStringAsync("/.well-known/jwks.json", Ct);

        second.Should().Be(first);
    }

    [Test]
    public async Task Jwks_IsNotServedBySpaFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcpal-wwwroot-" + Guid.NewGuid().ToString("N"));
        var wwwroot = Path.Combine(root, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "index.html"), "<!doctype html><title>MCPal SPA</title>", Ct);
        try
        {
            await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, webRoot: wwwroot);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/.well-known/jwks.json", Ct);

            var body = await response.Content.ReadAsStringAsync(Ct);
            body.Should().NotContain("MCPal SPA");
            body.Should().Contain("\"keys\"");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
