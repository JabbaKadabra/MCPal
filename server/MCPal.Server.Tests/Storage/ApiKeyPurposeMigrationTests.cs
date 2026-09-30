using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace MCPal.Server.Tests.Storage;

[TestFixture]
internal sealed class ApiKeyPurposeMigrationTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private const string InitialMigration = "20260930072220_InitialCreate";

    [Test]
    public async Task Migrate_KeyCreatedBeforePurposeExisted_BecomesAnyKeyWithoutRestrictions()
    {
        var connectionString = await PostgresFixture.CreateEmptyDatabaseAsync(Ct);
        var options = new DbContextOptionsBuilder<MCPalDbContext>().UseNpgsql(connectionString).Options;
        var companyId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        await using (var db = new MCPalDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(InitialMigration, Ct);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO "Companies" ("Id", "Name", "Slug", "CreatedAt", "Disabled") VALUES (@company, 'Old', 'old', now(), false);
                INSERT INTO "ApiKeys" ("Id", "CompanyId", "Name", "Prefix", "KeyHash", "CreatedAt", "Disabled") VALUES (@key, @company, 'legacy', 'mcpal_x', 'hash', now(), false);
                """;
            command.Parameters.AddWithValue("company", companyId);
            command.Parameters.AddWithValue("key", keyId);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using (var db = new MCPalDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var verify = new MCPalDbContext(options);
        var key = await verify.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == keyId, Ct);
        key.Purpose.Should().Be(ApiKeyPurpose.Any);
        key.AllowedServers.Should().BeEmpty();
    }
}
