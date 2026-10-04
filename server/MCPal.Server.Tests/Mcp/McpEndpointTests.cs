using System.Net;
using System.Net.Http.Headers;
using MCPal.Server.Tests.Infrastructure;
using MCPal.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MCPal.Server.Tests.Mcp;

[TestFixture]
internal sealed class McpEndpointTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static async Task<McpClient> ConnectAsync(ServerWebApplicationFactory factory, string apiKey)
    {
        var httpClient = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(factory.Server.BaseAddress, "mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + apiKey },
            },
            httpClient,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static string TextOf(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    [Test]
    public async Task Post_WithoutCredential_Returns401WithChallenge()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var client = factory.CreateClient();
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(new Uri("/mcp", UriKind.Relative), content, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("resource_metadata=");
    }

    [Test]
    public async Task ListTools_BridgeRegistered_ReturnsToolsWithPublicNamesAndServerPrefix()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search", "get"), _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        tools.Select(t => t.Name).Should().BeEquivalentTo("kb__get", "kb__search");
        tools.First(t => t.Name == "kb__search").Description.Should().StartWith("[kb] ");
    }

    [Test]
    public async Task ListTools_BridgeRegisteredToolWithInvalidSchema_ListsTheOtherTools()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var catalog = new BridgeCatalog("fake", "1.0", ProtocolVersion.Current, [new ServerCatalog("kb", [
            new ToolDescriptor("search", null, "Find", "{\"type\":\"object\"}", null),
            new ToolDescriptor("broken", null, "Bad", "not json", null),
        ])]);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, catalog, _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        tools.Select(t => t.Name).Should().Equal("kb__search");
        bridge.RegisterResult?.RejectedTools.Should().ContainSingle().Which.ToolName.Should().Be("broken");
    }

    [Test]
    public async Task CallTool_BridgeRegistered_RelaysArgumentsAndReturnsBridgeResult()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        CallToolRequest? seen = null;
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search"), request =>
        {
            seen = request;
            return Task.FromResult(FakeBridge.Text("found it"));
        }, Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var result = await client.CallToolAsync("kb__search", new Dictionary<string, object?> { ["text"] = "hello" }, cancellationToken: Ct);

        result.IsError.Should().NotBe(true);
        TextOf(result).Should().Be("found it");
        seen.Should().NotBeNull();
        seen.ServerName.Should().Be("kb");
        seen.ToolName.Should().Be("search");
        seen.ArgumentsJson.Should().Contain("hello");
    }

    [Test]
    public async Task CallTool_BridgeReturnsStructuredContentAndMeta_ArrivesInMcpResult()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var response = new CallToolResponse(false, "[{\"type\":\"text\",\"text\":\"21.5\"}]", null, "{\"temp\":21.5}", "{\"source\":\"station-7\"}");
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("wx", "weather"), _ => Task.FromResult(response), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var result = await client.CallToolAsync("wx__weather", cancellationToken: Ct);

        result.StructuredContent?.GetProperty("temp").GetDouble().Should().Be(21.5);
        result.Meta?["source"]?.GetValue<string>().Should().Be("station-7");
    }

    [Test]
    public async Task CallTool_BridgeReturnsInvalidStructuredContent_DropsItAndKeepsContent()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var response = new CallToolResponse(false, "[{\"type\":\"text\",\"text\":\"ok\"}]", null, "{broken", "[not an object");
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("wx", "weather"), _ => Task.FromResult(response), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var result = await client.CallToolAsync("wx__weather", cancellationToken: Ct);

        result.IsError.Should().NotBe(true);
        TextOf(result).Should().Be("ok");
        result.StructuredContent.Should().BeNull();
    }

    [Test]
    public async Task ListTools_ToolWithOutputSchema_ExposesIt()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var catalog = new BridgeCatalog("fake", "1.0", ProtocolVersion.Current, [new ServerCatalog("wx", [
            new ToolDescriptor("weather", null, "W", "{\"type\":\"object\"}", null, "{\"type\":\"object\",\"properties\":{\"temp\":{\"type\":\"number\"}}}"),
        ])]);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, catalog, _ => Task.FromResult(FakeBridge.Text("x")), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        tools.Single().ProtocolTool.OutputSchema?.GetProperty("properties").GetProperty("temp").GetProperty("type").GetString().Should().Be("number");
    }

    [Test]
    public async Task CallTool_BridgeAnswersMoreThanTheMessageLimit_ReturnsErrorLongBeforeTheTimeout()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var oversize = FakeBridge.Text(new string('x', 11 * 1024 * 1024));
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "big"), _ => Task.FromResult(oversize), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await client.CallToolAsync("kb__big", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task CallTool_BridgeReportsError_ReturnsIsError()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "search"),
            _ => Task.FromResult(new CallToolResponse(true, "[]", "backend exploded")), Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var result = await client.CallToolAsync("kb__search", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Be("backend exploded");
    }

    [Test]
    public async Task CallTool_NoBridgeOnline_ReturnsNotAvailableError()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var result = await client.CallToolAsync("kb__search", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Contain("not available");
    }

    [Test]
    public async Task CallTool_BridgeTooSlow_ReturnsTimeoutError()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:ToolCallTimeoutSeconds"] = "1" });
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        await using var bridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "slow"), async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), Ct);
            return FakeBridge.Text("late");
        }, Ct);
        await using var client = await ConnectAsync(factory, acme.PersonalKey);

        var result = await client.CallToolAsync("kb__slow", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Contain("timed out after 1 s");
    }

    [Test]
    public async Task ListTools_TwoCompanies_EachSeesOnlyOwnTools()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var globex = await factory.SeedCompanyAsync("Globex", Ct);
        await using var acmeBridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "secret"), _ => Task.FromResult(FakeBridge.Text("acme")), Ct);
        await using var globexBridge = await FakeBridge.StartAsync(factory, globex.BridgeKey, FakeBridge.CatalogWith("wiki", "read"), _ => Task.FromResult(FakeBridge.Text("globex")), Ct);
        await using var acmeClient = await ConnectAsync(factory, acme.PersonalKey);
        await using var globexClient = await ConnectAsync(factory, globex.PersonalKey);

        var acmeTools = await acmeClient.ListToolsAsync(cancellationToken: Ct);
        var globexTools = await globexClient.ListToolsAsync(cancellationToken: Ct);

        acmeTools.Select(t => t.Name).Should().Equal("kb__secret");
        globexTools.Select(t => t.Name).Should().Equal("wiki__read");
    }

    [Test]
    public async Task CallTool_NameOfOtherCompany_ReturnsNotAvailableAndNeverReachesOtherBridge()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        var globex = await factory.SeedCompanyAsync("Globex", Ct);
        var reached = false;
        await using var acmeBridge = await FakeBridge.StartAsync(factory, acme.BridgeKey, FakeBridge.CatalogWith("kb", "secret"), _ =>
        {
            reached = true;
            return Task.FromResult(FakeBridge.Text("acme data"));
        }, Ct);
        await using var globexClient = await ConnectAsync(factory, globex.PersonalKey);

        var result = await globexClient.CallToolAsync("kb__secret", cancellationToken: Ct);

        result.IsError.Should().BeTrue();
        reached.Should().BeFalse();
    }

    [Test]
    public async Task Post_ExceedingRateLimit_Returns429()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:McpRequestsPerMinute"] = "2" });
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 4; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acme.PersonalKey);
            using var response = await client.SendAsync(request, Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
        statuses[0].Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task Post_DistinctInvalidTokens_ShareOneLimitAndGet429()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:McpRequestsPerMinute"] = "2" });
        using var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 4; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"garbage-{Guid.NewGuid()}");
            using var response = await client.SendAsync(request, Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task Post_ValidKeyAfterInvalidTokensExhaustedIpLimit_IsStillServed()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:McpRequestsPerMinute"] = "2" });
        var acme = await factory.SeedCompanyAsync("Acme", Ct);
        using var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            using var garbage = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            garbage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"garbage-{i}");
            (await client.SendAsync(garbage, Ct)).Dispose();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acme.PersonalKey);
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests).And.NotBe(HttpStatusCode.Unauthorized);
    }
}
