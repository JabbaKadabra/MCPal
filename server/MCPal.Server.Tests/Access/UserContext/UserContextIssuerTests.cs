using System.Text.Json;
using Autofac;
using MCPal.Server.Access;
using MCPal.Server.Access.UserContext;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MCPal.Server.Tests.Access.UserContext;

[TestFixture]
internal sealed class UserContextIssuerTests : ServerTestBase
{
    private static readonly Guid CompanyId = Guid.Parse("7f3c6a1e-5b7d-4f0e-9a51-0c2d2f6f7a10");

    private static UserPolicy Anna(string? displayName = "Anna Example", PortalRole role = PortalRole.Member) =>
        new("user-anna", "anna@acme.example", displayName, role, ["Everyone", "hr"], []);

    private static async Task<TokenValidationResult> VerifyAsync(SigningKeyStore store, string token, string audience, FakeTimeProvider time)
    {
        var jwks = string.Join(',', (await store.GetPublishedKeysAsync(Ct)).Select(k => k.PublicJwkJson));
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "https://mcpal.example.com",
            ValidAudience = audience,
            ValidTypes = [UserContextIssuer.TokenType],
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKeys = new JsonWebKeySet($$"""{"keys":[{{jwks}}]}""").GetSigningKeys(),
            LifetimeValidator = (notBefore, expires, _, _) => notBefore <= time.GetUtcNow() && time.GetUtcNow() < expires,
        };
        return await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
    }

    private static Dictionary<string, string?> Settings() => new() { ["Mcpal:PublicUrl"] = "https://mcpal.example.com/" };

    [Test]
    public async Task IssueAsync_Token_VerifiesAgainstTheJwksWithTypAudienceAndIssuer()
    {
        await using var scope = await GetServicesAsync(settings: Settings());
        var time = scope.Resolve<FakeTimeProvider>();
        var store = scope.Resolve<SigningKeyStore>();
        var issuer = scope.Resolve<UserContextIssuer>();

        var token = await issuer.IssueAsync(Anna(), CompanyId, "acme", "hr-portal", "salaries", "req-1", "oauth", Ct);

        var result = await VerifyAsync(store, token, "mcpal:acme/hr-portal", time);
        result.IsValid.Should().BeTrue(result.Exception?.Message);
        var jwt = new JsonWebToken(token);
        jwt.Typ.Should().Be("mcpal-user+jwt");
        jwt.Alg.Should().Be("ES256");
        jwt.Kid.Should().Be((await store.GetSigningKeyAsync(Ct)).Kid);
    }

    [Test]
    public async Task IssueAsync_Token_CarriesTheDocumentedClaims()
    {
        await using var scope = await GetServicesAsync(settings: Settings());

        var token = await scope.Resolve<UserContextIssuer>().IssueAsync(Anna(), CompanyId, "acme", "hr-portal", "salaries", "req-1", "oauth", Ct);

        var jwt = new JsonWebToken(token);
        jwt.Issuer.Should().Be("https://mcpal.example.com");
        jwt.GetPayloadValue<string>("aud").Should().Be("mcpal:acme/hr-portal");
        jwt.Subject.Should().Be("user-anna");
        jwt.GetPayloadValue<string>("email").Should().Be("anna@acme.example");
        jwt.GetPayloadValue<string>("name").Should().Be("Anna Example");
        jwt.GetPayloadValue<string[]>("groups").Should().Equal("Everyone", "hr");
        jwt.GetPayloadValue<string>("mcpal_role").Should().Be("member");
        jwt.GetPayloadValue<string>("mcpal_company_id").Should().Be(CompanyId.ToString());
        jwt.GetPayloadValue<string>("mcpal_company").Should().Be("acme");
        jwt.GetPayloadValue<string>("mcpal_tool").Should().Be("salaries");
        jwt.GetPayloadValue<string>("mcpal_auth").Should().Be("oauth");
        jwt.Id.Should().Be("req-1");
    }

    [Test]
    public async Task IssueAsync_Times_ComeFromTheTimeProviderAndExpireAfterFiveMinutes()
    {
        await using var scope = await GetServicesAsync(settings: Settings());
        var time = scope.Resolve<FakeTimeProvider>();

        var token = await scope.Resolve<UserContextIssuer>().IssueAsync(Anna(), CompanyId, "acme", "hr", "t", "req-1", "pat", Ct);

        var jwt = new JsonWebToken(token);
        new DateTimeOffset(jwt.IssuedAt).Should().Be(time.GetUtcNow());
        new DateTimeOffset(jwt.ValidFrom).Should().Be(time.GetUtcNow());
        new DateTimeOffset(jwt.ValidTo).Should().Be(time.GetUtcNow().AddSeconds(300));
    }

    [Test]
    public async Task IssueAsync_ConfiguredLifetime_IsUsed()
    {
        await using var scope = await GetServicesAsync(settings: new() { ["Mcpal:UserContext:TokenLifetimeSeconds"] = "60" });
        var time = scope.Resolve<FakeTimeProvider>();

        var token = await scope.Resolve<UserContextIssuer>().IssueAsync(Anna(), CompanyId, "acme", "hr", "t", "req-1", "pat", Ct);

        new DateTimeOffset(new JsonWebToken(token).ValidTo).Should().Be(time.GetUtcNow().AddSeconds(60));
    }

    [Test]
    public async Task IssueAsync_Owner_HasRoleOwnerAndNameFallsBackToEmail()
    {
        await using var scope = await GetServicesAsync(settings: Settings());

        var token = await scope.Resolve<UserContextIssuer>().IssueAsync(Anna(displayName: null, role: PortalRole.Owner), CompanyId, "acme", "hr", "t", "req-1", "pat", Ct);

        var jwt = new JsonWebToken(token);
        jwt.GetPayloadValue<string>("mcpal_role").Should().Be("owner");
        jwt.GetPayloadValue<string>("name").Should().Be("anna@acme.example");
    }

    [Test]
    public async Task Token_AfterExpiryOrForOtherServerOrCompany_DoesNotVerify()
    {
        await using var scope = await GetServicesAsync(settings: Settings());
        var time = scope.Resolve<FakeTimeProvider>();
        var store = scope.Resolve<SigningKeyStore>();
        var token = await scope.Resolve<UserContextIssuer>().IssueAsync(Anna(), CompanyId, "acme", "hr-portal", "salaries", "req-1", "oauth", Ct);

        var otherServer = await VerifyAsync(store, token, "mcpal:acme/wiki", time);
        var otherCompany = await VerifyAsync(store, token, "mcpal:globex/hr-portal", time);
        time.Advance(TimeSpan.FromSeconds(301));
        var expired = await VerifyAsync(store, token, "mcpal:acme/hr-portal", time);

        otherServer.IsValid.Should().BeFalse();
        otherCompany.IsValid.Should().BeFalse();
        expired.IsValid.Should().BeFalse();
    }

    [Test]
    public async Task Token_SignedByTheRetiredKeyJustBeforeRotation_StillVerifiesDuringGrace()
    {
        await using var scope = await GetServicesAsync(settings: Settings());
        var time = scope.Resolve<FakeTimeProvider>();
        var store = scope.Resolve<SigningKeyStore>();
        var issuer = scope.Resolve<UserContextIssuer>();
        await store.GetSigningKeyAsync(Ct);
        time.Advance(TimeSpan.FromDays(90) - TimeSpan.FromSeconds(10));
        await store.RotateAsync(Ct);
        var token = await issuer.IssueAsync(Anna(), CompanyId, "acme", "hr", "t", "req-1", "pat", Ct);

        time.Advance(TimeSpan.FromSeconds(20));
        await store.RotateAsync(Ct);
        var result = await VerifyAsync(store, token, "mcpal:acme/hr", time);

        result.IsValid.Should().BeTrue(result.Exception?.Message);
    }

    [Test]
    public void Audience_Format_IsCompanySlashServer()
    {
        UserContextIssuer.Audience("acme", "hr-portal").Should().Be("mcpal:acme/hr-portal");
    }

    [Test]
    public async Task Jwks_Json_ContainsNoPrivateMembers()
    {
        await using var scope = await GetServicesAsync();
        var store = scope.Resolve<SigningKeyStore>();
        await store.GetSigningKeyAsync(Ct);

        foreach (var key in await store.GetPublishedKeysAsync(Ct))
        {
            using var json = JsonDocument.Parse(key.PublicJwkJson);
            json.RootElement.TryGetProperty("d", out _).Should().BeFalse();
        }
    }
}
