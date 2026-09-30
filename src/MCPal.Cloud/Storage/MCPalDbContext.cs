using MCPal.Cloud.OAuth;
using MCPal.Cloud.Tenancy;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Cloud.Storage;

internal sealed class MCPalDbContext(DbContextOptions<MCPalDbContext> options) : IdentityDbContext<PortalUser>(options)
{
    public DbSet<Company> Companies => Set<Company>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();

    public DbSet<AuthorizationCode> AuthorizationCodes => Set<AuthorizationCode>();

    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        builder.Entity<Company>(company =>
        {
            company.HasKey(c => c.Id);
            company.Property(c => c.Name).HasMaxLength(200);
            company.Property(c => c.Slug).HasMaxLength(100);
            company.HasIndex(c => c.Slug).IsUnique();
        });

        builder.Entity<ApiKey>(key =>
        {
            key.HasKey(k => k.Id);
            key.Property(k => k.Name).HasMaxLength(200);
            key.Property(k => k.Prefix).HasMaxLength(32);
            key.Property(k => k.KeyHash).HasMaxLength(64);
            key.HasIndex(k => k.KeyHash).IsUnique();
            key.HasIndex(k => k.CompanyId);
            key.HasOne<Company>().WithMany().HasForeignKey(k => k.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PortalUser>(user =>
        {
            user.HasIndex(u => u.CompanyId);
            user.HasOne<Company>().WithMany().HasForeignKey(u => u.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<OAuthClient>(client =>
        {
            client.HasKey(c => c.ClientId);
            client.Property(c => c.ClientId).HasMaxLength(64);
            client.Property(c => c.ClientName).HasMaxLength(200);
        });

        builder.Entity<AuthorizationCode>(code =>
        {
            code.HasKey(c => c.CodeHash);
            code.Property(c => c.CodeHash).HasMaxLength(64);
            code.HasIndex(c => c.ExpiresAt);
        });

        builder.Entity<OAuthToken>(token =>
        {
            token.HasKey(t => t.Hash);
            token.Property(t => t.Hash).HasMaxLength(64);
            token.HasIndex(t => t.ExpiresAt);
            token.HasIndex(t => t.ApiKeyId);
        });
    }
}
