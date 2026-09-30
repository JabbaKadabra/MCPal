using System.Net;
using System.Text.Json;
using MCPal.Cloud.Tests.Infrastructure;

namespace MCPal.Cloud.Tests.Audit;

[TestFixture]
internal sealed class AuditEndpointTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static async Task<Guid> SignupAsync(PortalClient portal, string company, string email)
    {
        using var response = await portal.SignupAsync(company, email, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await PortalClient.JsonAsync(response, Ct)).GetProperty("companyId").GetGuid();
    }

    private static async Task<JsonElement> GetJsonAsync(PortalClient portal, string url)
    {
        using var response = await portal.GetAsync(url, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await PortalClient.JsonAsync(response, Ct);
    }

    private static string[] Tools(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("toolName").GetString() ?? string.Empty)];

    [Test]
    public async Task Get_Unauthenticated_Returns401()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);

        using var response = await portal.GetAsync("/api/portal/audit", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Get_RowsOfCompany_ReturnsNewestFirstWithKeyName()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        var companyId = await SignupAsync(portal, "Acme", "a@acme.example");
        var keyId = await CreateKeyAsync(portal, "ci-key");
        await AuditTestData.InsertAsync(factory.Services, Ct,
            AuditTestData.Entry(companyId, Base, tool: "first", apiKeyId: keyId),
            AuditTestData.Entry(companyId, Base.AddMinutes(1), tool: "second", outcome: "timeout", apiKeyId: keyId),
            AuditTestData.Entry(companyId, Base.AddMinutes(2), tool: "third", authKind: "oauth", oauthClientId: "claude-1"));

        var page = await GetJsonAsync(portal, "/api/portal/audit");

        Tools(page).Should().Equal("third", "second", "first");
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items[1].GetProperty("apiKeyName").GetString().Should().Be("ci-key");
        items[1].GetProperty("outcome").GetString().Should().Be("timeout");
        items[0].GetProperty("oauthClientId").GetString().Should().Be("claude-1");
        page.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Test]
    public async Task Get_MoreRowsThanLimit_PagesWithCursorWithoutGapsOrDuplicates()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        var companyId = await SignupAsync(portal, "Acme", "a@acme.example");
        // Five rows share one timestamp: the cursor must break the tie by id.
        var same = Enumerable.Range(0, 5).Select(i => AuditTestData.Entry(companyId, Base, tool: $"same{i}")).ToArray();
        var others = Enumerable.Range(0, 4).Select(i => AuditTestData.Entry(companyId, Base.AddMinutes(-(i + 1)), tool: $"older{i}")).ToArray();
        await AuditTestData.InsertAsync(factory.Services, Ct, [.. same, .. others]);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await GetJsonAsync(portal, "/api/portal/audit?limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            seen.AddRange(Tools(page));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        seen.Should().HaveCount(9).And.OnlyHaveUniqueItems();
        seen.Take(5).Should().OnlyContain(t => t.StartsWith("same", StringComparison.Ordinal));
        seen.Skip(5).Should().Equal("older0", "older1", "older2", "older3");
    }

    [Test]
    public async Task Get_LimitAboveCap_IsCappedAt200()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        var companyId = await SignupAsync(portal, "Acme", "a@acme.example");
        await AuditTestData.InsertAsync(factory.Services, Ct, [.. Enumerable.Range(0, 205).Select(i => AuditTestData.Entry(companyId, Base.AddSeconds(i)))]);

        var page = await GetJsonAsync(portal, "/api/portal/audit?limit=100000");

        page.GetProperty("items").GetArrayLength().Should().Be(200);
        page.GetProperty("nextCursor").GetString().Should().NotBeNull();
    }

    [Test]
    public async Task Get_Filters_NarrowTheResult()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        var companyId = await SignupAsync(portal, "Acme", "a@acme.example");
        var keyId = await CreateKeyAsync(portal, "k1");
        await AuditTestData.InsertAsync(factory.Services, Ct,
            AuditTestData.Entry(companyId, Base, tool: "search", apiKeyId: keyId),
            AuditTestData.Entry(companyId, Base.AddHours(1), tool: "get_page", outcome: "tool_error"),
            AuditTestData.Entry(companyId, Base.AddHours(2), tool: "search", outcome: "timeout"),
            AuditTestData.Entry(companyId, Base.AddHours(3), tool: "sea_rch_50%"));

        Tools(await GetJsonAsync(portal, "/api/portal/audit?tool=search")).Should().Equal("search", "search");
        Tools(await GetJsonAsync(portal, "/api/portal/audit?tool=SEARCH")).Should().Equal("search", "search");
        Tools(await GetJsonAsync(portal, "/api/portal/audit?tool=" + Uri.EscapeDataString("50%"))).Should().Equal("sea_rch_50%");
        // An underscore is a literal character, not the single-character wildcard of LIKE: "s_a" must not match "sea".
        Tools(await GetJsonAsync(portal, "/api/portal/audit?tool=" + Uri.EscapeDataString("s_a"))).Should().BeEmpty();
        Tools(await GetJsonAsync(portal, "/api/portal/audit?tool=" + Uri.EscapeDataString("a_r"))).Should().Equal("sea_rch_50%");
        Tools(await GetJsonAsync(portal, "/api/portal/audit?outcome=tool_error")).Should().Equal("get_page");
        Tools(await GetJsonAsync(portal, $"/api/portal/audit?keyId={keyId}")).Should().Equal("search");
        Tools(await GetJsonAsync(portal, "/api/portal/audit?from=" + Uri.EscapeDataString(Base.AddMinutes(30).ToString("o", System.Globalization.CultureInfo.InvariantCulture)) + "&to=" + Uri.EscapeDataString(Base.AddHours(2).ToString("o", System.Globalization.CultureInfo.InvariantCulture))))
            .Should().Equal("search", "get_page");
    }

    [TestCase("outcome=nonsense")]
    [TestCase("keyId=not-a-guid")]
    [TestCase("cursor=garbage")]
    [TestCase("from=yesterday")]
    [TestCase("limit=abc")]
    public async Task Get_InvalidParameter_Returns400(string query)
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        using var portal = new PortalClient(factory);
        await SignupAsync(portal, "Acme", "a@acme.example");

        using var response = await portal.GetAsync("/api/portal/audit?" + query, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Get_RowsOfOtherCompany_AreNeverReturned()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        using var acme = new PortalClient(factory);
        using var globex = new PortalClient(factory);
        var acmeId = await SignupAsync(acme, "Acme", "a@acme.example");
        var globexId = await SignupAsync(globex, "Globex", "g@globex.example");
        var globexKey = await CreateKeyAsync(globex, "globex-key");
        await AuditTestData.InsertAsync(factory.Services, Ct,
            AuditTestData.Entry(acmeId, Base, tool: "acme-tool"),
            AuditTestData.Entry(globexId, Base, tool: "globex-secret", apiKeyId: globexKey));

        var all = await GetJsonAsync(acme, "/api/portal/audit");
        var byForeignKey = await GetJsonAsync(acme, $"/api/portal/audit?keyId={globexKey}");
        var byForeignTool = await GetJsonAsync(acme, "/api/portal/audit?tool=globex-secret");

        Tools(all).Should().Equal("acme-tool");
        Tools(byForeignKey).Should().BeEmpty();
        Tools(byForeignTool).Should().BeEmpty();
    }

    [Test]
    public async Task Get_CursorOfOtherCompany_CannotLeakItsRows()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        using var acme = new PortalClient(factory);
        using var globex = new PortalClient(factory);
        var acmeId = await SignupAsync(acme, "Acme", "a@acme.example");
        var globexId = await SignupAsync(globex, "Globex", "g@globex.example");
        await AuditTestData.InsertAsync(factory.Services, Ct,
            [.. Enumerable.Range(0, 3).Select(i => AuditTestData.Entry(globexId, Base.AddMinutes(i), tool: $"g{i}")), AuditTestData.Entry(acmeId, Base, tool: "a0")]);
        var globexCursor = (await GetJsonAsync(globex, "/api/portal/audit?limit=1")).GetProperty("nextCursor").GetString() ?? string.Empty;

        var page = await GetJsonAsync(acme, "/api/portal/audit?cursor=" + Uri.EscapeDataString(globexCursor));

        Tools(page).Should().NotContain(t => t.StartsWith('g'));
    }

    [Test]
    public async Task ExportCsv_WithFilters_StreamsRowsAndNeutralisesFormulas()
    {
        await using var factory = await CloudWebApplicationFactory.CreateAsync(Ct);
        using var acme = new PortalClient(factory);
        using var globex = new PortalClient(factory);
        var acmeId = await SignupAsync(acme, "Acme", "a@acme.example");
        var globexId = await SignupAsync(globex, "Globex", "g@globex.example");
        var failing = AuditTestData.Entry(acmeId, Base.AddMinutes(1), tool: "get,page", outcome: "tool_error");
        failing.ErrorMessage = "=HYPERLINK(\"http://evil\")";
        await AuditTestData.InsertAsync(factory.Services, Ct,
            AuditTestData.Entry(acmeId, Base, tool: "search"),
            failing,
            AuditTestData.Entry(globexId, Base, tool: "globex-secret"));

        using var response = await acme.GetAsync("/api/portal/audit/export.csv", Ct);
        var csv = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition?.DispositionType.Should().Be("attachment");
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("occurredAt,durationMs,authKind,apiKeyId,apiKeyName,oauthClientId,agentName,serverName,toolName,publicName,outcome,errorMessage");
        lines.Should().HaveCount(3);
        csv.Should().Contain("\"get,page\"");
        csv.Should().Contain("'=HYPERLINK");
        csv.Should().NotContain("globex-secret");

        using var filtered = await acme.GetAsync("/api/portal/audit/export.csv?outcome=tool_error", Ct);
        (await filtered.Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
    }

    private static async Task<Guid> CreateKeyAsync(PortalClient portal, string name)
    {
        using var response = await portal.PostAsync("/api/portal/keys", new { name }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await PortalClient.JsonAsync(response, Ct)).GetProperty("id").GetGuid();
    }
}
