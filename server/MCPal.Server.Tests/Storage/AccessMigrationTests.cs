using MCPal.Server.Storage;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace MCPal.Server.Tests.Storage;

[TestFixture]
internal sealed class AccessMigrationTests
{
    private const string BeforeAccessGroups = "20260930175726_UserBoundCredentials";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task Migrate_ExistingCompanies_GetAnEveryoneGroupThatMayUseEverything()
    {
        var connectionString = await PostgresFixture.CreateEmptyDatabaseAsync(Ct);
        var options = new DbContextOptionsBuilder<MCPalDbContext>().UseNpgsql(connectionString).Options;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using (var db = new MCPalDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeAccessGroups, Ct);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO "Companies" ("Id", "Name", "Slug", "CreatedAt", "Disabled") VALUES (@first, 'One', 'one', now(), false), (@second, 'Two', 'two', now(), false);
                """;
            command.Parameters.AddWithValue("first", first);
            command.Parameters.AddWithValue("second", second);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using (var db = new MCPalDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var verify = new MCPalDbContext(options);
        var groups = await verify.AccessGroups.AsNoTracking().ToListAsync(Ct);
        groups.Should().HaveCount(2).And.OnlyContain(g => g.IsEveryone && g.Name == "Everyone");
        groups.Select(g => g.CompanyId).Should().BeEquivalentTo([first, second]);
        var grants = await verify.AccessGrants.AsNoTracking().ToListAsync(Ct);
        grants.Should().HaveCount(2).And.OnlyContain(g => g.ServerPattern == "*" && g.ToolPatterns.Length == 1 && g.ToolPatterns[0] == "*");
        grants.Select(g => g.GroupId).Should().BeEquivalentTo(groups.Select(g => g.Id));
    }
}
