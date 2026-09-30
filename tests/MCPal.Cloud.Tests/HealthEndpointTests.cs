using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MCPal.Cloud.Tests;

[TestFixture]
internal sealed class HealthEndpointTests
{
    [Test]
    public async Task Health_WhenRequested_ReturnsHealthy()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).Should().Be("Healthy");
    }
}
