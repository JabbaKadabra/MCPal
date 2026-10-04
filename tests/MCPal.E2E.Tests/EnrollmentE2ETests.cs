using MCPal.Bridge.Enrollment;
using MCPal.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace MCPal.E2E.Tests;

/// <summary>A bridge that only knows an enrollment code gets its key, connects and serves a tool call.</summary>
[TestFixture]
internal sealed class EnrollmentE2ETests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task Enroll_ThenConnect_ServesToolsWithTheEnrolledKey()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        string code;
        await using (var scope = stack.Services.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<BridgeEnrollmentService>().CreateAsync(acme.CompanyId, acme.OwnerUserId, Ct);
            code = created?.Code ?? throw new InvalidOperationException("No code created.");
        }

        var directory = Path.Combine(Path.GetTempPath(), "mcpal-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var credentials = Path.Combine(directory, "credentials.json");
            using var http = stack.CreateClient();

            var key = await BridgeEnroller.EnrollAsync(http, "http://localhost", code, "hq-enrolled", credentials, Ct);

            BridgeCredentials.TryRead(credentials).Should().Be(key);
            using var bridge = await stack.StartBridgeAsync(acme with { BridgeKey = key }, "test", "hq-enrolled", Ct);
            await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
            await E2EStack.WaitForToolAsync(client, "test__echo", Ct);
            var echo = await client.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "hi" }, cancellationToken: Ct);
            echo.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text.Should().Be("echo: hi");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
