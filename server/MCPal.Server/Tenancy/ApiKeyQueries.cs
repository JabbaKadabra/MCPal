namespace MCPal.Server.Tenancy;

internal static class ApiKeyQueries
{
    /// <summary>
    /// The one definition of a usable key: not revoked, not expired, its company not disabled and, for a personal key, its
    /// user of the same company and not disabled. Every credential check (API keys, open tunnels) filters through this.
    /// </summary>
    public static IQueryable<ApiKey> Active(this IQueryable<ApiKey> keys, IQueryable<Company> companies, IQueryable<PortalUser> users, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(users);

        return keys.Where(k => !k.Disabled
            && (k.ExpiresAt == null || k.ExpiresAt > now)
            && companies.Any(c => c.Id == k.CompanyId && !c.Disabled)
            && (k.UserId == null || users.Any(u => u.Id == k.UserId && u.CompanyId == k.CompanyId && !u.Disabled)));
    }

    /// <summary>The one definition of a user who may act: not disabled and the company not disabled.</summary>
    public static IQueryable<PortalUser> Active(this IQueryable<PortalUser> users, IQueryable<Company> companies)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(companies);

        return users.Where(u => !u.Disabled && companies.Any(c => c.Id == u.CompanyId && !c.Disabled));
    }
}
