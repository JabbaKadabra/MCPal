using MCPal.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace MCPal.E2E.Tests;

internal static class TemplateMigrator
{
    public static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MCPalDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new MCPalDbContext(options);
        await db.Database.MigrateAsync();
    }
}
