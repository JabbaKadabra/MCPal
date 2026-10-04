using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using MCPal.Server.Access.UserContext;
using MCPal.Server.Storage;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace MCPal.Server.Tests.Access.UserContext;

[TestFixture]
internal sealed class SigningKeyStoreTests : ServerTestBase
{
    /// <summary>Two hosts of the same MCPal server: one database, and one Data Protection key ring when <paramref name="keyRing"/> is shared.</summary>
    private async Task<(ILifetimeScope First, ILifetimeScope Second)> TwoInstancesAsync(string? secondKeyRing = null)
    {
        var keyRing = Directory.CreateTempSubdirectory("mcpal-keyring-").FullName;
        var first = await GetServicesAsync(settings: new() { ["Mcpal:DataProtectionPath"] = keyRing });
        var connectionString = first.Resolve<Microsoft.Extensions.Configuration.IConfiguration>()["ConnectionStrings:Mcpal"];
        var second = await GetServicesAsync(settings: new()
        {
            ["Mcpal:DataProtectionPath"] = secondKeyRing ?? keyRing,
            ["ConnectionStrings:Mcpal"] = connectionString,
        });
        return (first, second);
    }

    [Test]
    public async Task GetSigningKeyAsync_NoKeys_CreatesOneActiveKeyAndPersistsIt()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();

        var key = await scope.Resolve<SigningKeyStore>().GetSigningKeyAsync(Ct);

        var stored = await scope.Resolve<MCPalDbContext>().SigningKeys.AsNoTracking().SingleAsync(Ct);
        stored.Kid.Should().Be(key.Kid);
        stored.Algorithm.Should().Be("ES256");
        stored.ActivatesAt.Should().Be(time.GetUtcNow());
        stored.RetiresAt.Should().Be(time.GetUtcNow().AddDays(90));
        stored.RemoveAt.Should().Be(time.GetUtcNow().AddDays(97));
    }

    [Test]
    public async Task GetSigningKeyAsync_FirstCall_StoresThePrivateKeyProtectedAndThePublicKeyWithoutPrivateMembers()
    {
        await using var scope = await GetServicesAsync();
        await scope.Resolve<SigningKeyStore>().GetSigningKeyAsync(Ct);

        var stored = await scope.Resolve<MCPalDbContext>().SigningKeys.AsNoTracking().SingleAsync(Ct);

        // Not a usable PKCS#8 key as stored: it is wrapped by Data Protection.
        var importPlain = () => ECDsa.Create().ImportPkcs8PrivateKey(Convert.FromBase64String(stored.ProtectedPrivateKey), out _);
        importPlain.Should().Throw<CryptographicException>();
        using var jwk = JsonDocument.Parse(stored.PublicJwkJson);
        jwk.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kty", "crv", "x", "y", "kid", "use", "alg");
        jwk.RootElement.GetProperty("kid").GetString().Should().Be(stored.Kid);
    }

    [Test]
    public async Task GetSigningKeyAsync_SecondInstanceWithSameDatabaseAndKeyRing_UsesTheSameKey()
    {
        var (first, second) = await TwoInstancesAsync();
        await using var _ = first;
        await using var __ = second;

        var fromFirst = await first.Resolve<SigningKeyStore>().GetSigningKeyAsync(Ct);
        var fromSecond = await second.Resolve<SigningKeyStore>().GetSigningKeyAsync(Ct);

        fromSecond.Kid.Should().Be(fromFirst.Kid);
        (await first.Resolve<MCPalDbContext>().SigningKeys.CountAsync(Ct)).Should().Be(1);
    }

    [Test]
    public async Task GetSigningKeyAsync_ManyConcurrentCallsAtStartup_CreateExactlyOneKey()
    {
        await using var scope = await GetServicesAsync();
        var store = scope.Resolve<SigningKeyStore>();

        var keys = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.GetSigningKeyAsync(Ct)));

        keys.Select(k => k.Kid).Distinct().Should().ContainSingle();
        (await scope.Resolve<MCPalDbContext>().SigningKeys.CountAsync(Ct)).Should().Be(1);
    }

    [Test]
    public async Task GetSigningKeyAsync_TwoInstancesStartingTogether_CreateExactlyOneKey()
    {
        var (first, second) = await TwoInstancesAsync();
        await using var _ = first;
        await using var __ = second;

        var keys = await Task.WhenAll(first.Resolve<SigningKeyStore>().GetSigningKeyAsync(Ct), second.Resolve<SigningKeyStore>().GetSigningKeyAsync(Ct));

        keys[0].Kid.Should().Be(keys[1].Kid);
        (await first.Resolve<MCPalDbContext>().SigningKeys.CountAsync(Ct)).Should().Be(1);
    }

    [Test]
    public async Task GetSigningKeyAsync_InstanceWithLostKeyRing_CannotReadTheKeyAndCreatesANewOne()
    {
        var otherKeyRing = Directory.CreateTempSubdirectory("mcpal-other-keyring-").FullName;
        var (first, second) = await TwoInstancesAsync(otherKeyRing);
        await using var _ = first;
        await using var __ = second;
        var original = await first.Resolve<SigningKeyStore>().GetSigningKeyAsync(Ct);

        var replacement = await second.Resolve<SigningKeyStore>().GetSigningKeyAsync(Ct);

        replacement.Kid.Should().NotBe(original.Kid);
        (await second.Resolve<MCPalDbContext>().SigningKeys.CountAsync(Ct)).Should().Be(2);
    }

    [Test]
    public async Task RotateAsync_WithinTwoDaysOfRetirement_PrePublishesTheSuccessorWhichSignsAfterActivation()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();
        var store = scope.Resolve<SigningKeyStore>();
        var original = await store.GetSigningKeyAsync(Ct);

        time.Advance(TimeSpan.FromDays(88.5));
        await store.RotateAsync(Ct);
        var beforeRetirement = await store.GetSigningKeyAsync(Ct);
        var published = await store.GetPublishedKeysAsync(Ct);
        time.Advance(TimeSpan.FromDays(2));
        await store.RotateAsync(Ct);
        var afterRetirement = await store.GetSigningKeyAsync(Ct);

        beforeRetirement.Kid.Should().Be(original.Kid);
        published.Should().HaveCount(2);
        published.Select(k => k.Kid).Should().Contain(original.Kid);
        afterRetirement.Kid.Should().NotBe(original.Kid);
        afterRetirement.Kid.Should().Be(published.Single(k => k.Kid != original.Kid).Kid);
    }

    [Test]
    public async Task RotateAsync_FarFromRetirement_CreatesNoSuccessor()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();
        var store = scope.Resolve<SigningKeyStore>();
        await store.GetSigningKeyAsync(Ct);

        time.Advance(TimeSpan.FromDays(30));
        await store.RotateAsync(Ct);
        await store.RotateAsync(Ct);

        (await scope.Resolve<MCPalDbContext>().SigningKeys.CountAsync(Ct)).Should().Be(1);
    }

    [Test]
    public async Task RotateAsync_CalledRepeatedlyNearRetirement_CreatesTheSuccessorOnlyOnce()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();
        var store = scope.Resolve<SigningKeyStore>();
        await store.GetSigningKeyAsync(Ct);

        time.Advance(TimeSpan.FromDays(89));
        await store.RotateAsync(Ct);
        await store.RotateAsync(Ct);
        time.Advance(TimeSpan.FromHours(1));
        await store.RotateAsync(Ct);

        (await scope.Resolve<MCPalDbContext>().SigningKeys.CountAsync(Ct)).Should().Be(2);
    }

    [Test]
    public async Task RotateAsync_RetiredKey_StaysInTheJwksForSevenDaysThenIsRemoved()
    {
        await using var scope = await GetServicesAsync();
        var time = scope.Resolve<FakeTimeProvider>();
        var store = scope.Resolve<SigningKeyStore>();
        var original = await store.GetSigningKeyAsync(Ct);
        time.Advance(TimeSpan.FromDays(89));
        await store.RotateAsync(Ct);

        time.Advance(TimeSpan.FromDays(5));
        await store.RotateAsync(Ct);
        var duringGrace = (await store.GetPublishedKeysAsync(Ct)).Select(k => k.Kid).ToList();
        time.Advance(TimeSpan.FromDays(3));
        await store.RotateAsync(Ct);
        var afterGrace = (await store.GetPublishedKeysAsync(Ct)).Select(k => k.Kid).ToList();

        duringGrace.Should().Contain(original.Kid).And.HaveCount(2);
        afterGrace.Should().NotContain(original.Kid).And.HaveCount(1);
        (await scope.Resolve<MCPalDbContext>().SigningKeys.AnyAsync(k => k.Kid == original.Kid, Ct)).Should().BeFalse();
    }

    [Test]
    public async Task GetPublishedKeysAsync_AfterReloadInterval_PicksUpAKeyRotatedByAnotherInstance()
    {
        var (first, second) = await TwoInstancesAsync();
        await using var _ = first;
        await using var __ = second;
        var firstStore = first.Resolve<SigningKeyStore>();
        await firstStore.GetSigningKeyAsync(Ct);
        var firstTime = first.Resolve<FakeTimeProvider>();
        var secondTime = second.Resolve<FakeTimeProvider>();
        firstTime.Advance(TimeSpan.FromDays(89));
        secondTime.Advance(TimeSpan.FromDays(89));
        await second.Resolve<SigningKeyStore>().RotateAsync(Ct);

        firstTime.Advance(SigningKeyStore.ReloadInterval + TimeSpan.FromSeconds(1));
        secondTime.Advance(SigningKeyStore.ReloadInterval + TimeSpan.FromSeconds(1));
        var published = await firstStore.GetPublishedKeysAsync(Ct);

        published.Should().HaveCount(2);
    }
}
