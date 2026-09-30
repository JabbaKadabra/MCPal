using System.Net;
using MCPal.Cloud.Tests.Infrastructure;

namespace MCPal.Cloud.Tests;

[TestFixture]
internal sealed class HealthEndpointTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    /// <summary>A connection string that points to a closed port, like a stopped PostgreSQL.</summary>
    private static Dictionary<string, string?> UnreachableDatabase() =>
        new() { ["ConnectionStrings:Mcpal"] = "Host=127.0.0.1;Port=1;Database=mcpal;Username=x;Password=x;Timeout=2;Command Timeout=2" };

    [TestCase("/health")]
    [TestCase("/health/ready")]
    [TestCase("/health/live")]
    public async Task Get_HealthEndpointWithDatabase_ReturnsHealthy(string path)
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("Healthy");
    }

    [TestCase("/health")]
    [TestCase("/health/ready")]
    public async Task Get_ReadyEndpointWithoutDatabase_Returns503WithStatusOnly(string path)
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct, settings: UnreachableDatabase());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("Unhealthy");
    }

    [Test]
    public async Task Get_LiveEndpointWithoutDatabase_StaysHealthy()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct, settings: UnreachableDatabase());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
