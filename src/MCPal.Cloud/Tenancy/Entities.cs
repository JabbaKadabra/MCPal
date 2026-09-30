using Microsoft.AspNetCore.Identity;

namespace MCPal.Cloud.Tenancy;

internal sealed class Company
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public bool Disabled { get; set; }
}

internal sealed class ApiKey
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>First characters of the raw key, safe to display.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 of the raw key, hex encoded.</summary>
    public string KeyHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public bool Disabled { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}

internal sealed class PortalUser : IdentityUser
{
    public Guid CompanyId { get; set; }
}
