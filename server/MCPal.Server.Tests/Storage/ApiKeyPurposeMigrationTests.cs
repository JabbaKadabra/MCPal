using MCPal.Server.Storage;
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
    private const string PurposeMigration = "20260930130830_ApiKeyPurposeAndAllowedServers";

    [Test]
    public async Task Migrate_KeyCreatedBeforePurposeExisted_BecomesUnrestrictedKeyOfPurposeZero()
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
            // The entity model has moved on since (purposes were renumbered, server lists removed), so the state is read as plain SQL.
            await db.GetService<IMigrator>().MigrateAsync(PurposeMigration, Ct);
        }

        await using var verify = new NpgsqlConnection(connectionString);
        await verify.OpenAsync(Ct);
        await using var query = verify.CreateCommand();
        query.CommandText = """SELECT "Purpose", cardinality("AllowedServers") FROM "ApiKeys" WHERE "Id" = @key""";
        query.Parameters.AddWithValue("key", keyId);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        reader.GetInt32(0).Should().Be(0);
        reader.GetInt32(1).Should().Be(0);
    }
}
