using MCPal.Cloud.Storage;
using MCPal.Cloud.Tenancy;
using MCPal.Cloud.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace MCPal.Cloud.Tests.Storage;

[TestFixture]
internal sealed class AccountMigrationTests
{
    private const string BeforeConfirmation = "20260930130830_ApiKeyPurposeAndAllowedServers";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task Migrate_UserCreatedBeforeEmailConfirmationExisted_IsConfirmedSoNobodyIsLockedOut()
    {
        var connectionString = await PostgresFixture.CreateEmptyDatabaseAsync(Ct);
        var options = new DbContextOptionsBuilder<MCPalDbContext>().UseNpgsql(connectionString).Options;
        var companyId = Guid.NewGuid();
        await using (var db = new MCPalDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeConfirmation, Ct);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO "Companies" ("Id", "Name", "Slug", "CreatedAt", "Disabled") VALUES (@company, 'Old', 'old', now(), false);
                INSERT INTO "AspNetUsers" ("Id", "CompanyId", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ('u1', @company, 'old@acme.example', 'OLD@ACME.EXAMPLE', 'old@acme.example', 'OLD@ACME.EXAMPLE', false, false, false, true, 0);
                """;
            command.Parameters.AddWithValue("company", companyId);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using (var db = new MCPalDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var verify = new MCPalDbContext(options);
        (await verify.Users.AsNoTracking().SingleAsync(u => u.Id == "u1", Ct)).EmailConfirmed.Should().BeTrue();
    }

    [Test]
    public async Task Migrate_UserAndKeyCreatedBeforeRoles_BecomeOwnerAndKeyWithoutCreator()
    {
        var connectionString = await PostgresFixture.CreateEmptyDatabaseAsync(Ct);
        var options = new DbContextOptionsBuilder<MCPalDbContext>().UseNpgsql(connectionString).Options;
        var companyId = Guid.NewGuid();
        await using (var db = new MCPalDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeConfirmation, Ct);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO "Companies" ("Id", "Name", "Slug", "CreatedAt", "Disabled") VALUES (@company, 'Old', 'old', now(), false);
                INSERT INTO "AspNetUsers" ("Id", "CompanyId", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ('u1', @company, 'old@acme.example', 'OLD@ACME.EXAMPLE', 'old@acme.example', 'OLD@ACME.EXAMPLE', true, false, false, true, 0);
                INSERT INTO "ApiKeys" ("Id", "CompanyId", "Name", "Prefix", "KeyHash", "CreatedAt", "Disabled", "Purpose", "AllowedServers")
                VALUES (gen_random_uuid(), @company, 'legacy', 'mcpal_x', 'hash', now(), false, 0, '{}');
                """;
            command.Parameters.AddWithValue("company", companyId);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using (var db = new MCPalDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var verify = new MCPalDbContext(options);
        (await verify.Users.AsNoTracking().SingleAsync(u => u.Id == "u1", Ct)).Role.Should().Be(PortalRole.Owner);
        (await verify.ApiKeys.AsNoTracking().SingleAsync(Ct)).CreatedByUserId.Should().BeNull();
    }
}
