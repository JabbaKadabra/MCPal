using Autofac;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Server.Tests.Tenancy;

[TestFixture]
internal sealed class BridgeEnrollmentServiceTests : ServerTestBase
{
    private static async Task<CreatedEnrollment> NewCodeAsync(BridgeEnrollmentService service, Guid companyId, string ownerId) =>
        await service.CreateAsync(companyId, ownerId, Ct) ?? throw new InvalidOperationException("No code created.");

    private static async Task<(Guid CompanyId, string OwnerId)> SeedAsync(ILifetimeScope scope, string name = "Acme", string email = "owner@acme.example")
    {
        var company = await scope.Resolve<ICompanyService>().CreateAsync(name, Ct);
        var owner = await CreateUserAsync(scope, company.Id, email, PortalRole.Owner);
        return (company.Id, owner.Id);
    }

    [Test]
    public async Task CreateAsync_Owner_ReturnsCodeWithPrefixAndExpiry()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var now = scope.Resolve<FakeTimeProvider>().GetUtcNow();

        var created = await scope.Resolve<BridgeEnrollmentService>().CreateAsync(companyId, ownerId, Ct);

        created.Should().NotBeNull();
        created.Code.Should().MatchRegex("^mcpale_[A-Za-z0-9]{24}$");
        created.ExpiresAt.Should().Be(now.AddMinutes(15));
    }

    [Test]
    public async Task CreateAsync_NewCode_StoresOnlyHash()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);

        var created = await NewCodeAsync(scope.Resolve<BridgeEnrollmentService>(), companyId, ownerId);

        var stored = await scope.Resolve<MCPalDbContext>().BridgeEnrollments.AsNoTracking().SingleAsync(Ct);
        stored.CodeHash.Should().Be(ApiKeyService.Hash(created.Code)).And.NotContain(created.Code);
        stored.CompanyId.Should().Be(companyId);
        stored.CreatedByUserId.Should().Be(ownerId);
        stored.RedeemedAt.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_ElevenOpenCodes_RefusesTheEleventh()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        for (var i = 0; i < BridgeEnrollmentService.MaxOpenCodes; i++)
        {
            (await service.CreateAsync(companyId, ownerId, Ct)).Should().NotBeNull();
        }

        var eleventh = await service.CreateAsync(companyId, ownerId, Ct);

        eleventh.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_OtherCompanyHasTenOpenCodes_IsNotAffected()
    {
        await using var scope = await GetServicesAsync();
        var (acme, acmeOwner) = await SeedAsync(scope);
        var (globex, globexOwner) = await SeedAsync(scope, "Globex", "owner@globex.example");
        var service = scope.Resolve<BridgeEnrollmentService>();
        for (var i = 0; i < BridgeEnrollmentService.MaxOpenCodes; i++)
        {
            await service.CreateAsync(acme, acmeOwner, Ct);
        }

        (await service.CreateAsync(globex, globexOwner, Ct)).Should().NotBeNull();
    }

    [Test]
    public async Task CreateAsync_OldCodesExpired_DeletesThemAndAllowsNewOnes()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        for (var i = 0; i < BridgeEnrollmentService.MaxOpenCodes; i++)
        {
            await service.CreateAsync(companyId, ownerId, Ct);
        }

        scope.Resolve<FakeTimeProvider>().Advance(TimeSpan.FromMinutes(16));
        var created = await NewCodeAsync(service, companyId, ownerId);

        created.Should().NotBeNull();
        (await scope.Resolve<MCPalDbContext>().BridgeEnrollments.AsNoTracking().CountAsync(Ct)).Should().Be(1);
    }

    [Test]
    public async Task RedeemAsync_ValidCode_CreatesBridgeKeyOfTheCompany()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);

        var redeemed = await service.RedeemAsync(created.Code, "hq-01", Ct);

        redeemed.Should().NotBeNull();
        var validated = await scope.Resolve<IApiKeyService>().ValidateAsync(redeemed.ApiKey, Ct);
        validated.Should().NotBeNull();
        validated.CompanyId.Should().Be(companyId);
        validated.Purpose.Should().Be(ApiKeyPurpose.Bridge);
        var db = scope.Resolve<MCPalDbContext>();
        var key = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == validated.ApiKeyId, Ct);
        key.Name.Should().Be("Bridge hq-01");
        key.CreatedByUserId.Should().Be(ownerId);
        var enrollment = await db.BridgeEnrollments.AsNoTracking().SingleAsync(Ct);
        enrollment.RedeemedAt.Should().NotBeNull();
        enrollment.ApiKeyId.Should().Be(key.Id);
    }

    [Test]
    public async Task RedeemAsync_SecondTime_ReturnsNull()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);
        await service.RedeemAsync(created.Code, "hq-01", Ct);

        var second = await service.RedeemAsync(created.Code, "hq-02", Ct);

        second.Should().BeNull();
        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().CountAsync(Ct)).Should().Be(1);
    }

    [Test]
    public async Task RedeemAsync_Expired_ReturnsNull()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);
        scope.Resolve<FakeTimeProvider>().Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        (await service.RedeemAsync(created.Code, "hq-01", Ct)).Should().BeNull();
    }

    [Test]
    public async Task RedeemAsync_TwoAtTheSameTime_CreateExactlyOneKey()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var created = await NewCodeAsync(scope.Resolve<BridgeEnrollmentService>(), companyId, ownerId);
        await using var first = scope.BeginLifetimeScope();
        await using var second = scope.BeginLifetimeScope();

        var results = await Task.WhenAll(
            first.Resolve<BridgeEnrollmentService>().RedeemAsync(created.Code, "a", Ct),
            second.Resolve<BridgeEnrollmentService>().RedeemAsync(created.Code, "b", Ct));

        results.Count(r => r is not null).Should().Be(1);
        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().CountAsync(Ct)).Should().Be(1);
    }

    [Test]
    public async Task RedeemAsync_CodeOfCompanyA_KeyBelongsToCompanyAOnly()
    {
        await using var scope = await GetServicesAsync();
        var (acme, acmeOwner) = await SeedAsync(scope);
        var (globex, _) = await SeedAsync(scope, "Globex", "owner@globex.example");
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, acme, acmeOwner);

        var redeemed = await service.RedeemAsync(created.Code, "hq-01", Ct);

        redeemed.Should().NotBeNull();
        var validated = await scope.Resolve<IApiKeyService>().ValidateAsync(redeemed.ApiKey, Ct);
        validated.Should().NotBeNull();
        validated.CompanyId.Should().Be(acme);
        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().CountAsync(k => k.CompanyId == globex, Ct)).Should().Be(0);
    }

    [Test]
    public async Task RedeemAsync_CreatorDisabledAfterwards_ReturnsNullAndCreatesNoKey()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);
        await scope.Resolve<MCPalDbContext>().Users.Where(u => u.Id == ownerId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Disabled, true), Ct);

        (await service.RedeemAsync(created.Code, "hq-01", Ct)).Should().BeNull();

        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().CountAsync(Ct)).Should().Be(0);
    }

    [Test]
    public async Task RedeemAsync_CompanyDisabledAfterwards_ReturnsNull()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);
        await scope.Resolve<MCPalDbContext>().Companies.Where(c => c.Id == companyId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Disabled, true), Ct);

        (await service.RedeemAsync(created.Code, "hq-01", Ct)).Should().BeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("garbage")]
    [TestCase("mcpale_neverIssuedNeverIssued1")]
    [TestCase("mcpal_0123abcd_wrongPrefixWrongPrefixWrongPrefixWrong")]
    public async Task RedeemAsync_UnknownOrMalformedCode_ReturnsNull(string? code)
    {
        await using var scope = await GetServicesAsync();
        await SeedAsync(scope);

        (await scope.Resolve<BridgeEnrollmentService>().RedeemAsync(code, "hq-01", Ct)).Should().BeNull();
    }

    [TestCase(null, "Bridge bridge")]
    [TestCase("", "Bridge bridge")]
    [TestCase("  hq 01  ", "Bridge hq 01")]
    [TestCase("a\nb\u0007c", "Bridge abc")]
    public async Task RedeemAsync_BridgeName_IsCleanedForTheKeyName(string? bridgeName, string expectedKeyName)
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);

        await service.RedeemAsync(created.Code, bridgeName, Ct);

        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().SingleAsync(Ct)).Name.Should().Be(expectedKeyName);
    }

    [Test]
    public async Task RedeemAsync_VeryLongBridgeName_IsTruncated()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);

        await service.RedeemAsync(created.Code, new string('x', 500), Ct);

        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().SingleAsync(Ct)).Name.Should().HaveLength("Bridge ".Length + 100);
    }

    [Test]
    public async Task RedeemAsync_NameCutInsideASurrogatePair_StillRedeemsAndStoresValidText()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);

        var redeemed = await service.RedeemAsync(created.Code, new string('x', 99) + "\U0001F600\U0001F600", Ct);

        redeemed.Should().NotBeNull();
        var name = (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().SingleAsync(Ct)).Name;
        name.Should().StartWith("Bridge " + new string('x', 99) + "\U0001F600");
        name.EnumerateRunes().Should().NotContain(rune => rune == System.Text.Rune.ReplacementChar);
    }

    [Test]
    public async Task RedeemAsync_NameWithFormatCharacters_StripsThem()
    {
        await using var scope = await GetServicesAsync();
        var (companyId, ownerId) = await SeedAsync(scope);
        var service = scope.Resolve<BridgeEnrollmentService>();
        var created = await NewCodeAsync(service, companyId, ownerId);

        await service.RedeemAsync(created.Code, "a\u202Eb\u200Bc", Ct);

        (await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().SingleAsync(Ct)).Name.Should().Be("Bridge abc");
    }
}
