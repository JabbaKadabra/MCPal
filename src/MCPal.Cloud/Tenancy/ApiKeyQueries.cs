namespace MCPal.Cloud.Tenancy;

internal static class ApiKeyQueries
{
    /// <summary>
    /// The one definition of a usable key: not revoked, not expired and its company not disabled. Every credential check
    /// (API keys, OAuth tokens issued from a key, open tunnels) filters through this.
    /// </summary>
    public static IQueryable<ApiKey> Active(this IQueryable<ApiKey> keys, IQueryable<Company> companies, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(companies);

        return keys.Where(k => !k.Disabled
            && (k.ExpiresAt == null || k.ExpiresAt > now)
            && companies.Any(c => c.Id == k.CompanyId && !c.Disabled));
    }
}
