using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace MCPal.Server.Tests.Storage;

[TestFixture]
internal sealed class UserBoundCredentialsMigrationTests
{
    private const string BeforeUserBoundCredentials = "20260930172352_RenameAgentToBridge";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static readonly Guid CompanyId = Guid.NewGuid();
    private static readonly Guid OtherCompanyId = Guid.NewGuid();
    private static readonly Guid AnyKey = Guid.NewGuid();
    private static readonly Guid BridgeKey = Guid.NewGuid();
    private static readonly Guid ClientKeyWithCreator = Guid.NewGuid();
    private static readonly Guid ClientKeyWithoutCreator = Guid.NewGuid();
    private static readonly Guid ClientKeyOfForeignCreator = Guid.NewGuid();

    /// <summary>Builds the database as it was before the migration: a company with users and keys of every old purpose, plus OAuth rows.</summary>
    private static async Task<DbContextOptions<MCPalDbContext>> PrepareLegacyDatabaseAsync()
    {
        var connectionString = await PostgresFixture.CreateEmptyDatabaseAsync(Ct);
        var options = new DbContextOptionsBuilder<MCPalDbContext>().UseNpgsql(connectionString).Options;
        await using (var db = new MCPalDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeUserBoundCredentials, Ct);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "Companies" ("Id", "Name", "Slug", "CreatedAt", "Disabled") VALUES (@company, 'Old', 'old', now(), false), (@other, 'Other', 'other', now(), false);
            INSERT INTO "AspNetUsers" ("Id", "CompanyId", "Role", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('anna', @company, 'Owner', 'anna@old.example', 'ANNA@OLD.EXAMPLE', 'anna@old.example', 'ANNA@OLD.EXAMPLE', true, false, false, true, 0),
                   ('eve', @other, 'Owner', 'eve@other.example', 'EVE@OTHER.EXAMPLE', 'eve@other.example', 'EVE@OTHER.EXAMPLE', true, false, false, true, 0);
            INSERT INTO "ApiKeys" ("Id", "CompanyId", "Name", "Prefix", "KeyHash", "CreatedAt", "Disabled", "Purpose", "AllowedServers", "CreatedByUserId") VALUES
                (@any, @company, 'any', 'mcpal_a', 'h1', now(), false, 0, '{}', 'anna'),
                (@bridge, @company, 'bridge', 'mcpal_b', 'h2', now(), false, 1, '{}', 'anna'),
                (@clientCreator, @company, 'client', 'mcpal_c', 'h3', now(), false, 2, '{jira}', 'anna'),
                (@clientNobody, @company, 'orphan', 'mcpal_d', 'h4', now(), false, 2, '{}', NULL),
                (@clientForeign, @company, 'foreign', 'mcpal_e', 'h5', now(), false, 2, '{}', 'eve');
            INSERT INTO "OAuthTokens" ("Hash", "Kind", "CompanyId", "ApiKeyId", "ClientId", "ExpiresAt", "Revoked") VALUES ('t1', 0, @company, @clientCreator, 'claude', now() + interval '1 hour', false);
            INSERT INTO "AuthorizationCodes" ("CodeHash", "ClientId", "CompanyId", "ApiKeyId", "RedirectUri", "CodeChallenge", "Scope", "ExpiresAt", "Used") VALUES ('c1', 'claude', @company, NULL, 'https://claude.ai/cb', 'x', 'mcp', now() + interval '1 hour', false);
            """;
        command.Parameters.AddWithValue("company", CompanyId);
        command.Parameters.AddWithValue("other", OtherCompanyId);
        command.Parameters.AddWithValue("any", AnyKey);
        command.Parameters.AddWithValue("bridge", BridgeKey);
        command.Parameters.AddWithValue("clientCreator", ClientKeyWithCreator);
        command.Parameters.AddWithValue("clientNobody", ClientKeyWithoutCreator);
        command.Parameters.AddWithValue("clientForeign", ClientKeyOfForeignCreator);
        await command.ExecuteNonQueryAsync(Ct);
        return options;
    }

    [Test]
    public async Task Migrate_AnyKey_BecomesBridgeKeyWithoutUser()
    {
        var options = await PrepareLegacyDatabaseAsync();
        await using (var db = new MCPalDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var verify = new MCPalDbContext(options);
        var key = await verify.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == AnyKey, Ct);
        key.Purpose.Should().Be(ApiKeyPurpose.Bridge);
        key.UserId.Should().BeNull();
    }

    [Test]
    public async Task Migrate_BridgeKey_StaysBridgeKey()
    {
        var options = await PrepareLegacyDatabaseAsync();
        await using (var db = new MCPalDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var verify = new MCPalDbContext(options);
        var key = await verify.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == BridgeKey, Ct);
        key.Purpose.Should().Be(ApiKeyPurpose.Bridge);
        key.UserId.Should().BeNull();
    }

    [Test]
    public async Task Migrate_ClientKeyWithCreator_BecomesPersonalKeyOfCreator()
    {
        var options = await PrepareLegacyDatabaseAsync();
        await using (var db = new MCPalDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var verify = new MCPalDbContext(options);
        var key = await verify.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == ClientKeyWithCreator, Ct);
        key.Purpose.Should().Be(ApiKeyPurpose.Personal);
        key.UserId.Should().Be("anna");
    }

    [Test]
    public async Task Migrate_ClientKeyWithoutUsableCreator_IsDeleted()
    {
        var options = await PrepareLegacyDatabaseAsync();
        await using (var db = new MCPalDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var verify = new MCPalDbContext(options);
        var remaining = await verify.ApiKeys.AsNoTracking().Select(k => k.Id).ToListAsync(Ct);
        remaining.Should().NotContain(ClientKeyWithoutCreator).And.NotContain(ClientKeyOfForeignCreator);
        remaining.Should().BeEquivalentTo([AnyKey, BridgeKey, ClientKeyWithCreator]);
    }

    [Test]
    public async Task Migrate_OAuthTokensAndCodes_AreDeletedSoClientsAuthorizeAgain()
    {
        var options = await PrepareLegacyDatabaseAsync();
        await using (var db = new MCPalDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var verify = new MCPalDbContext(options);
        (await verify.OAuthTokens.AnyAsync(Ct)).Should().BeFalse();
        (await verify.AuthorizationCodes.AnyAsync(Ct)).Should().BeFalse();
    }
}
