using Microsoft.AspNetCore.Identity;

namespace MCPal.Server.Tenancy;

internal sealed class Company
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public bool Disabled { get; set; }
}

/// <summary>What a key may be used for. Every key has exactly one purpose.</summary>
internal enum ApiKeyPurpose
{
    /// <summary>A personal access token: bound to one portal user, works on <c>/mcp</c> with that user's rights. Refused on tunnels.</summary>
    Personal = 0,

    /// <summary>Belongs to the company: only opens bridge tunnels. Refused on <c>/mcp</c>.</summary>
    Bridge = 1,
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

    /// <summary>The portal user a <see cref="ApiKeyPurpose.Personal"/> key acts as. Null for bridge keys.</summary>
    public string? UserId { get; set; }

    /// <summary>The portal user who created the key. Null for keys created before users had roles.</summary>
    public string? CreatedByUserId { get; set; }
}

/// <summary>
/// A single-use code with which a bridge fetches its own bridge key. The code is stored hashed and shown once, to the owner who creates it.
/// </summary>
internal sealed class BridgeEnrollment
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    /// <summary>SHA-256 of the code, hex encoded.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>The owner who created the code; the key made on redemption lists this user as its creator.</summary>
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RedeemedAt { get; set; }

    /// <summary>The bridge key created on redemption.</summary>
    public Guid? ApiKeyId { get; set; }
}

/// <summary>Owners manage users, groups, bridge keys and see the audit log; members use their allowed tools, see connections and create personal access tokens for themselves.</summary>
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

    /// <summary>Shown in audit log and passed to local MCP servers. Falls back to the email address when empty.</summary>
    public string? DisplayName { get; set; }

    /// <summary>A disabled user cannot sign in, and their tokens and personal access tokens stop working. The account and its settings stay.</summary>
    public bool Disabled { get; set; }

    /// <summary>Issuer of the external identity provider (SSO) this user is linked to. Reserved for SSO.</summary>
    public string? ExternalIssuer { get; set; }

    /// <summary>Subject of the external identity (SSO). Reserved for SSO.</summary>
    public string? ExternalSubject { get; set; }
}
