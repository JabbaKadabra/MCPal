using MCPal.Server.Access;
using MCPal.Server.Access.UserContext;
using MCPal.Server.Audit;
using MCPal.Server.OAuth;
using MCPal.Server.Portal;
using MCPal.Server.Ports;
using MCPal.Server.Tenancy;
using MCPal.Server.Tunnel;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MCPal.Server.Storage;

internal sealed class MCPalDbContext(DbContextOptions<MCPalDbContext> options) : IdentityDbContext<PortalUser>(options), IMcpalData
{
    public DbSet<Company> Companies => Set<Company>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();

    public DbSet<AuthorizationCode> AuthorizationCodes => Set<AuthorizationCode>();

    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();

    public DbSet<ToolCallAudit> ToolCallAudits => Set<ToolCallAudit>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<BridgeEnrollment> BridgeEnrollments => Set<BridgeEnrollment>();

    public DbSet<AccessGroup> AccessGroups => Set<AccessGroup>();

    public DbSet<AccessGroupMember> AccessGroupMembers => Set<AccessGroupMember>();

    public DbSet<AccessGrant> AccessGrants => Set<AccessGrant>();

    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();

    IQueryable<T> IMcpalData.Query<T>() => Set<T>();

    void IMcpalData.Add<T>(T entity) => Set<T>().Add(entity);

    void IMcpalData.AddRange<T>(IEnumerable<T> entities) => Set<T>().AddRange(entities);

    void IMcpalData.Remove<T>(T entity) => Set<T>().Remove(entity);

    void IMcpalData.RemoveRange<T>(IEnumerable<T> entities) => Set<T>().RemoveRange(entities);

    async Task<IDataTransaction> IMcpalData.BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfDataTransaction(this, await Database.BeginTransactionAsync(cancellationToken));

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
            key.HasIndex(k => k.UserId);
            key.HasOne<PortalUser>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
            key.HasIndex(k => k.KeyHash).IsUnique();
            key.HasIndex(k => k.CompanyId);
            key.HasOne<Company>().WithMany().HasForeignKey(k => k.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PortalUser>(user =>
        {
            user.HasIndex(u => u.CompanyId);
            user.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
            user.Property(u => u.DisplayName).HasMaxLength(200);
            user.Property(u => u.ExternalIssuer).HasMaxLength(500);
            user.Property(u => u.ExternalSubject).HasMaxLength(500);
            user.HasOne<Company>().WithMany().HasForeignKey(u => u.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Invitation>(invitation =>
        {
            invitation.HasKey(i => i.Id);
            invitation.Property(i => i.Email).HasMaxLength(256);
            invitation.Property(i => i.Role).HasConversion<string>().HasMaxLength(16);
            invitation.Property(i => i.TokenHash).HasMaxLength(64);
            invitation.HasIndex(i => i.TokenHash).IsUnique();
            invitation.HasIndex(i => new { i.CompanyId, i.Email });
            invitation.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BridgeEnrollment>(enrollment =>
        {
            enrollment.HasKey(e => e.Id);
            enrollment.Property(e => e.CodeHash).HasMaxLength(64);
            enrollment.Property(e => e.CreatedByUserId).HasMaxLength(450);
            enrollment.HasIndex(e => e.CodeHash).IsUnique();
            enrollment.HasIndex(e => e.CompanyId);
            enrollment.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AccessGroup>(group =>
        {
            group.HasKey(g => g.Id);
            group.Property(g => g.Name).HasMaxLength(AccessGroup.MaxNameLength);
            group.Property(g => g.ExternalId).HasMaxLength(500);
            group.HasIndex(g => new { g.CompanyId, g.Name });
            // One implicit Everyone group per company.
            group.HasIndex(g => g.CompanyId).IsUnique().HasDatabaseName("IX_AccessGroups_Everyone").HasFilter("\"IsEveryone\"");
            group.HasOne<Company>().WithMany().HasForeignKey(g => g.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AccessGroupMember>(member =>
        {
            member.HasKey(m => new { m.GroupId, m.UserId });
            member.HasIndex(m => new { m.CompanyId, m.UserId });
            member.HasOne<AccessGroup>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            member.HasOne<PortalUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            member.HasOne<Company>().WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AccessGrant>(grant =>
        {
            grant.HasKey(g => g.Id);
            grant.Property(g => g.ServerPattern).HasMaxLength(ToolNaming.MaxServerNameLength);
            grant.Property(g => g.ToolPatterns).HasColumnType("text[]");
            grant.HasIndex(g => new { g.CompanyId, g.GroupId });
            grant.HasOne<AccessGroup>().WithMany().HasForeignKey(g => g.GroupId).OnDelete(DeleteBehavior.Cascade);
            grant.HasOne<Company>().WithMany().HasForeignKey(g => g.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SigningKey>(key =>
        {
            key.HasKey(k => k.Kid);
            key.Property(k => k.Kid).HasMaxLength(64);
            key.Property(k => k.Algorithm).HasMaxLength(16);
            key.HasIndex(k => k.RemoveAt);
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
            code.HasOne<PortalUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ToolCallAudit>(audit =>
        {
            audit.HasKey(a => a.Id);
            audit.Property(a => a.AuthKind).HasMaxLength(AuditColumns.AuthKind);
            audit.Property(a => a.UserId).HasMaxLength(AuditColumns.UserId);
            audit.Property(a => a.OAuthClientId).HasMaxLength(AuditColumns.OAuthClientId);
            audit.Property(a => a.BridgeName).HasMaxLength(AuditColumns.Name);
            audit.Property(a => a.ServerName).HasMaxLength(AuditColumns.Name);
            audit.Property(a => a.ToolName).HasMaxLength(AuditColumns.Name);
            audit.Property(a => a.PublicName).HasMaxLength(AuditColumns.PublicName);
            audit.Property(a => a.Outcome).HasMaxLength(AuditColumns.Outcome);
            audit.Property(a => a.ErrorMessage).HasMaxLength(AuditColumns.ErrorMessage);
            audit.HasIndex(a => new { a.CompanyId, a.OccurredAt });
            audit.HasIndex(a => a.OccurredAt);
            audit.HasIndex(a => new { a.CompanyId, a.UserId, a.OccurredAt });
            audit.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<OAuthToken>(token =>
        {
            token.HasKey(t => t.Hash);
            token.Property(t => t.Hash).HasMaxLength(64);
            token.HasIndex(t => t.ExpiresAt);
            token.HasIndex(t => t.UserId);
            token.HasOne<PortalUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>A PostgreSQL transaction on the context's connection.</summary>
internal sealed class EfDataTransaction(MCPalDbContext db, IDbContextTransaction transaction) : IDataTransaction
{
    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken) => transaction.RollbackAsync(cancellationToken);

    public async Task LockAsync(long key, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);

    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
