using Autofac;
using MCPal.Server.Tests.Infrastructure;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Server.Tests.Tenancy;

[TestFixture]
internal sealed class ApiKeyServiceTests : ServerTestBase
{
    [Test]
    public async Task CreateAsync_NewKey_ReturnsRawKeyInDocumentedFormat()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);

        var created = await scope.Resolve<IApiKeyService>().CreateAsync(company.Id, "hq", null, Ct);

        created.RawKey.Should().MatchRegex($"^mcpal_{company.Id.ToString("N")[..8]}_[A-Za-z0-9]{{40}}$");
        created.Prefix.Should().Be(created.RawKey[..21]);
    }

    [Test]
    public async Task CreateAsync_TwoKeysOfSameCompany_ShowDistinctPrefixesWithSecretCharacters()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var keys = scope.Resolve<IApiKeyService>();

        var laptop = await keys.CreateAsync(company.Id, "laptop", null, Ct);
        var server = await keys.CreateAsync(company.Id, "server", null, Ct);

        var companyPart = $"mcpal_{company.Id.ToString("N")[..8]}_";
        laptop.Prefix.Should().StartWith(companyPart).And.HaveLength(companyPart.Length + 6);
        laptop.Prefix.Should().NotBe(server.Prefix);
    }

    [Test]
    public async Task CreateAsync_NewKey_StoresOnlyHash()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);

        var created = await scope.Resolve<IApiKeyService>().CreateAsync(company.Id, "hq", null, Ct);

        var stored = await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().SingleAsync(Ct);
        stored.KeyHash.Should().Be(ApiKeyService.Hash(created.RawKey)).And.NotContain(created.RawKey);
        stored.Prefix.Should().Be(created.Prefix);
    }

    [Test]
    public async Task ValidateAsync_ValidKey_ReturnsCompanyAndKeyId()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var service = scope.Resolve<IApiKeyService>();
        var created = await service.CreateAsync(company.Id, "hq", null, Ct);

        var validated = await service.ValidateAsync(created.RawKey, Ct);

        validated.Should().NotBeNull();
        validated.CompanyId.Should().Be(company.Id);
        validated.ApiKeyId.Should().Be(created.Id);
        validated.Purpose.Should().Be(ApiKeyPurpose.Any);
        validated.AllowedServers.Should().BeEmpty();
    }

    [TestCase("")]
    [TestCase("garbage")]
    [TestCase("mcpal_12345678_notarealkey")]
    public async Task ValidateAsync_UnknownKey_ReturnsNull(string rawKey)
    {
        await using var scope = await GetServicesAsync();

        var validated = await scope.Resolve<IApiKeyService>().ValidateAsync(rawKey, Ct);

        validated.Should().BeNull();
    }

    [Test]
    public async Task ValidateAsync_ExpiredKey_ReturnsNull()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var service = scope.Resolve<IApiKeyService>();
        var created = await service.CreateAsync(company.Id, "hq", time.GetUtcNow().AddDays(1), Ct);

        time.Advance(TimeSpan.FromDays(2));

        (await service.ValidateAsync(created.RawKey, Ct)).Should().BeNull();
    }

    [Test]
    public async Task ValidateAsync_RevokedKey_ReturnsNull()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var service = scope.Resolve<IApiKeyService>();
        var created = await service.CreateAsync(company.Id, "hq", null, Ct);

        var revoked = await service.RevokeAsync(company.Id, created.Id, Ct);

        revoked.Should().BeTrue();
        (await service.ValidateAsync(created.RawKey, Ct)).Should().BeNull();
    }

    [Test]
    public async Task ValidateAsync_DisabledCompany_ReturnsNull()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var service = scope.Resolve<IApiKeyService>();
        var created = await service.CreateAsync(company.Id, "hq", null, Ct);
        await scope.Resolve<MCPalDbContext>().Companies.Where(c => c.Id == company.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Disabled, true), Ct);

        (await service.ValidateAsync(created.RawKey, Ct)).Should().BeNull();
    }

    [Test]
    public async Task ValidateAsync_ValidKey_UpdatesLastUsedAt()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var service = scope.Resolve<IApiKeyService>();
        var created = await service.CreateAsync(company.Id, "hq", null, Ct);

        await service.ValidateAsync(created.RawKey, Ct);

        var listed = await service.ListAsync(company.Id, Ct);
        listed.Should().ContainSingle().Which.LastUsedAt.Should().Be(time.GetUtcNow());
    }

    [Test]
    public async Task RevokeAsync_KeyOfOtherCompany_ReturnsFalseAndKeyStaysValid()
    {
        await using var scope = await GetServicesAsync();
        var companies = scope.Resolve<ICompanyService>();
        var owner = await companies.CreateAsync("Owner", Ct);
        var attacker = await companies.CreateAsync("Attacker", Ct);
        var service = scope.Resolve<IApiKeyService>();
        var created = await service.CreateAsync(owner.Id, "hq", null, Ct);

        var revoked = await service.RevokeAsync(attacker.Id, created.Id, Ct);

        revoked.Should().BeFalse();
        (await service.ValidateAsync(created.RawKey, Ct)).Should().NotBeNull();
    }

    [Test]
    public async Task ListAsync_TwoCompanies_ReturnsOnlyOwnKeys()
    {
        await using var scope = await GetServicesAsync();
        var companies = scope.Resolve<ICompanyService>();
        var first = await companies.CreateAsync("First", Ct);
        var second = await companies.CreateAsync("Second", Ct);
        var service = scope.Resolve<IApiKeyService>();
        await service.CreateAsync(first.Id, "a", null, Ct);
        await service.CreateAsync(second.Id, "b", null, Ct);

        var listed = await service.ListAsync(first.Id, Ct);

        listed.Should().ContainSingle().Which.Name.Should().Be("a");
    }

    [Test]
    public async Task RevokeAsync_ExistingKey_NotifiesListeners()
    {
        var listener = Substitute.For<IApiKeyRevocationListener>();
        await using var scope = await GetServicesAsync(b => b.RegisterInstance(listener).As<IApiKeyRevocationListener>());
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var service = scope.Resolve<IApiKeyService>();
        var created = await service.CreateAsync(company.Id, "hq", null, Ct);

        await service.RevokeAsync(company.Id, created.Id, Ct);

        await listener.Received(1).OnRevokedAsync(company.Id, created.Id, Ct);
    }

    [Test]
    public async Task CreateAsync_WithPurposeAndServers_PersistsThemAndValidateReturnsThem()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);
        var service = scope.Resolve<IApiKeyService>();

        var created = await service.CreateAsync(company.Id, "team", null, ApiKeyPurpose.Client, ["jira", "wiki"], Ct);

        var stored = await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().SingleAsync(k => k.Id == created.Id, Ct);
        stored.Purpose.Should().Be(ApiKeyPurpose.Client);
        stored.AllowedServers.Should().Equal("jira", "wiki");
        var validated = await service.ValidateAsync(created.RawKey, Ct);
        validated?.Purpose.Should().Be(ApiKeyPurpose.Client);
        validated?.AllowedServers.Should().Equal("jira", "wiki");
    }

    [Test]
    public async Task CreateAsync_DuplicateServersDifferingInCase_AreStoredOnce()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);

        var created = await scope.Resolve<IApiKeyService>().CreateAsync(company.Id, "team", null, ApiKeyPurpose.Client, ["jira", "JIRA", " jira "], Ct);

        var stored = await scope.Resolve<MCPalDbContext>().ApiKeys.AsNoTracking().SingleAsync(k => k.Id == created.Id, Ct);
        stored.AllowedServers.Should().Equal("jira");
    }

    [Test]
    public async Task CreateAsync_BridgeKeyWithServers_Throws()
    {
        await using var scope = await GetServicesAsync();
        var company = await scope.Resolve<ICompanyService>().CreateAsync("Acme", Ct);

        var act = async () => await scope.Resolve<IApiKeyService>().CreateAsync(company.Id, "bridge", null, ApiKeyPurpose.Bridge, ["jira"], Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
