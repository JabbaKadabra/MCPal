using System.Net;
using MCPal.Server.Tests.Infrastructure;

namespace MCPal.Server.Tests.Portal;

[TestFixture]
internal sealed class SpaHostingTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static string CreateWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcpal-wwwroot-" + Guid.NewGuid().ToString("N"));
        var wwwroot = Path.Combine(root, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<!doctype html><title>MCPal SPA</title>");
        return wwwroot;
    }

    [TestCase("/")]
    [TestCase("/keys")]
    [TestCase("/connections")]
    [TestCase("/audit")]
    [TestCase("/forgot-password")]
    [TestCase("/reset-password?email=a%40b.c&token=x")]
    [TestCase("/confirm-email?userId=1&token=x")]
    [TestCase("/oauth/authorize?client_id=x&redirect_uri=y")]
    public async Task Get_ClientSideRoute_ServesIndexHtml(string path)
    {
        var webRoot = CreateWebRoot();
        try
        {
            await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, webRoot: webRoot);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(path, Ct);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("MCPal SPA");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(webRoot) ?? webRoot, recursive: true);
        }
    }

    [TestCase("/api/portal/does-not-exist")]
    [TestCase("/api/unknown")]
    [TestCase("/oauth/token")]
    [TestCase("/oauth/register")]
    [TestCase("/.well-known/unknown")]
    [TestCase("/hub/other")]
    [TestCase("/health/ready")]
    [TestCase("/health/live")]
    [TestCase("/health/unknown")]
    public async Task Get_MachineEndpoint_IsNeverServedTheSpa(string path)
    {
        var webRoot = CreateWebRoot();
        try
        {
            await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, webRoot: webRoot);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(path, Ct);

            (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain("MCPal SPA");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(webRoot) ?? webRoot, recursive: true);
        }
    }
}
