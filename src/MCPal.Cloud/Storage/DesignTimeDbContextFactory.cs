using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MCPal.Cloud.Storage;

/// <summary>Used by <c>dotnet ef</c>; pinned to PostgreSQL, the production engine.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MCPalDbContext>
{
    public MCPalDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MCPalDbContext>()
            .UseNpgsql("Host=localhost;Database=mcpal_design;Username=postgres;Password=postgres")
            .Options;
        return new MCPalDbContext(options);
    }
}
