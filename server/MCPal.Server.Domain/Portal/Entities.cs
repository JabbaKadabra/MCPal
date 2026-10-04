using MCPal.Server.Tenancy;

namespace MCPal.Server.Portal;

/// <summary>A pending or accepted invitation of a colleague to a company. The token travels by mail and is stored hashed.</summary>
internal sealed class Invitation
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Email { get; set; } = string.Empty;

    public PortalRole Role { get; set; }

    /// <summary>SHA-256 of the token, hex encoded.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;
}
