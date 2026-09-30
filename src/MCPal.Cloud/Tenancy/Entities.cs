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

/// <summary>What a key may be used for. Existing keys are <see cref="Any"/>.</summary>
internal enum ApiKeyPurpose
{
    /// <summary>Opens tunnels and works as bearer token / OAuth login.</summary>
    Any = 0,

    /// <summary>Only opens agent tunnels. Refused on <c>/mcp</c> and at the OAuth authorize page.</summary>
    Agent = 1,

    /// <summary>Only for Claude: bearer token or OAuth login. Refused on tunnels.</summary>
    Client = 2,
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

    public ApiKeyPurpose Purpose { get; set; }

    /// <summary>Server names this key may use on <c>/mcp</c>. Empty means all servers. Only for <see cref="ApiKeyPurpose.Client"/> and <see cref="ApiKeyPurpose.Any"/>.</summary>
    public string[] AllowedServers { get; set; } = [];

    /// <summary>The portal user who created the key. Null for keys created before users had roles.</summary>
    public string? CreatedByUserId { get; set; }
}

/// <summary>Owners manage users and every key; members see connections, the audit log and connect info, and create Claude keys for themselves.</summary>
internal enum PortalRole
{
    Member = 0,
    Owner = 1,
}

internal sealed class PortalUser : IdentityUser
{
    public Guid CompanyId { get; set; }

    /// <summary>Stored as text. Every code path that creates a user sets it; the default is the least privileged role.</summary>
    public PortalRole Role { get; set; } = PortalRole.Member;
}
