using System.Text.Json;
using MCPal.Bridge.Config;
using MCPal.Server.Access;
using MCPal.Server.Access.UserContext;
using MCPal.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MCPal.E2E.Tests;

/// <summary>The caller travels from Claude's credential through server and bridge to the local MCP server, and access rules hold end to end.</summary>
[TestFixture]
internal sealed class UserContextE2ETests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static string TextOf(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    private static async Task<TokenValidationResult> VerifyAsync(string jwks, string token, string audience) =>
        await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "http://localhost:8080",
            ValidAudience = audience,
            ValidTypes = [UserContextIssuer.TokenType],
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
        });

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!File.Exists(path))
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    [Test]
    public async Task Whoami_PersonalKey_ReceivesTokenVerifiableWithJwks()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme Corp", Ct);
        var jwksFile = Path.Combine(Directory.CreateTempSubdirectory("mcpal-e2e-jwks-").FullName, "jwks.json");
        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct, jwksFile: jwksFile);
        await using var client = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(client, "test__whoami", Ct);

        var result = await client.CallToolAsync("test__whoami", cancellationToken: Ct);

        using var meta = JsonDocument.Parse(TextOf(result));
        meta.RootElement.GetProperty("sub").GetString().Should().Be(acme.OwnerUserId);
        meta.RootElement.GetProperty("email").GetString().Should().Be(acme.OwnerEmail);
        meta.RootElement.GetProperty("company").GetString().Should().Be("acme-corp");
        var token = meta.RootElement.GetProperty("token").GetString() ?? string.Empty;
        using var http = stack.CreateClient();
        var serverJwks = await http.GetStringAsync("/.well-known/jwks.json", Ct);
        (await VerifyAsync(serverJwks, token, "mcpal:acme-corp/test")).IsValid.Should().BeTrue();
        var jwt = new JsonWebToken(token);
        jwt.GetPayloadValue<string>("mcpal_tool").Should().Be("whoami");
        jwt.GetPayloadValue<string>("mcpal_role").Should().Be("owner");

        // The bridge's copy of the JWKS verifies the same token, for local servers without internet access.
        await WaitForFileAsync(jwksFile);
        var bridgeJwks = await File.ReadAllTextAsync(jwksFile, Ct);
        (await VerifyAsync(bridgeJwks, token, "mcpal:acme-corp/test")).IsValid.Should().BeTrue();
    }

    [Test]
    public async Task Whoami_TwoUsersInParallel_EachSeesOwnIdentity()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var anna = await stack.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await using var ownerClient = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await using var annaClient = await stack.ConnectClientAsync(anna.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(ownerClient, "test__whoami", Ct);

        var calls = Enumerable.Range(0, 12).Select(async i =>
        {
            var annaCall = i % 2 == 0;
            var result = await (annaCall ? annaClient : ownerClient).CallToolAsync("test__whoami", cancellationToken: Ct);
            using var meta = JsonDocument.Parse(TextOf(result));
            return (AnnaCall: annaCall, Sub: meta.RootElement.GetProperty("sub").GetString());
        });
        var results = await Task.WhenAll(calls);

        results.Where(r => r.AnnaCall).Should().OnlyContain(r => r.Sub == anna.UserId);
        results.Where(r => !r.AnnaCall).Should().OnlyContain(r => r.Sub == acme.OwnerUserId);
    }

    [Test]
    public async Task Whoami_HttpLocalServer_HeaderOnlyOnToolCalls()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var anna = await stack.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        await using var local = await HttpLocalMcpServer.StartAsync(Ct);
        var servers = new Dictionary<string, LocalServerConfig>
        {
            ["wiki"] = new(null, [], new Dictionary<string, string>(), local.Url.ToString(), new Dictionary<string, string>()) { UserTokenHeader = "Authorization" },
        };
        using var bridge = await stack.StartBridgeAsync(acme, "wiki", "hq-01", Ct, servers: servers);
        await using var annaClient = await stack.ConnectClientAsync(anna.PersonalKey, Ct);
        await using var ownerClient = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(annaClient, "wiki__whoami_http", Ct);

        var annaResult = await annaClient.CallToolAsync("wiki__whoami_http", cancellationToken: Ct);
        var ownerResult = await ownerClient.CallToolAsync("wiki__whoami_http", cancellationToken: Ct);

        TextOf(annaResult).Should().StartWith("Bearer ");
        TextOf(ownerResult).Should().StartWith("Bearer ");
        new JsonWebToken(TextOf(annaResult)["Bearer ".Length..]).Subject.Should().Be(anna.UserId);
        new JsonWebToken(TextOf(ownerResult)["Bearer ".Length..]).Subject.Should().Be(acme.OwnerUserId);
        var seen = local.Requests.ToList();
        seen.Where(r => r.Authorization is not null).Should().OnlyContain(r => r.RpcMethod == "tools/call");
        seen.Where(r => r.RpcMethod == "tools/call").Should().HaveCount(2).And.OnlyContain(r => r.Authorization != null);
        seen.Where(r => r.RpcMethod is "initialize" or "tools/list" or "notifications/initialized").Should().NotBeEmpty().And.OnlyContain(r => r.Authorization == null);
    }

    [Test]
    public async Task ForbiddenTool_HiddenAndUncallable_EndToEnd()
    {
        await using var stack = await E2EStack.CreateAsync(Ct);
        var acme = await stack.SeedCompanyAsync("Acme", Ct);
        var anna = await stack.SeedUserAsync(acme.CompanyId, "anna@acme.example", PortalRole.Member, Ct);
        await using (var scope = stack.Services.CreateAsyncScope())
        {
            var access = scope.ServiceProvider.GetRequiredService<AccessService>();
            var everyone = (await access.ListGroupsAsync(acme.CompanyId, Ct)).Single(g => g.IsEveryone);
            await access.UpdateGrantAsync(acme.CompanyId, everyone.Grants.Single().Id, "test", ["echo", "add"], Ct);
        }

        using var bridge = await stack.StartBridgeAsync(acme, "test", "hq-01", Ct);
        await using var annaClient = await stack.ConnectClientAsync(anna.PersonalKey, Ct);
        await using var ownerClient = await stack.ConnectClientAsync(acme.PersonalKey, Ct);
        await E2EStack.WaitForToolAsync(ownerClient, "test__whoami", Ct);

        var annaTools = (await annaClient.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
        var allowed = await annaClient.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "hi" }, cancellationToken: Ct);
        var forbidden = await annaClient.CallToolAsync("test__crash", cancellationToken: Ct);
        var unknown = await annaClient.CallToolAsync("test__nonexistent", cancellationToken: Ct);
        var ownerTools = (await ownerClient.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();

        annaTools.Should().BeEquivalentTo("test__echo", "test__add");
        TextOf(allowed).Should().Be("echo: hi");
        forbidden.IsError.Should().BeTrue();
        TextOf(forbidden).Should().Be(TextOf(unknown).Replace("test__nonexistent", "test__crash", StringComparison.Ordinal));
        ownerTools.Should().Contain(["test__echo", "test__add", "test__crash", "test__whoami"]);
    }
}
