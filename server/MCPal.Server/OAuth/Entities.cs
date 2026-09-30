namespace MCPal.Server.OAuth;

internal sealed class OAuthClient
{
    public string ClientId { get; set; } = string.Empty;

    public string ClientName { get; set; } = string.Empty;

    public string[] RedirectUris { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class AuthorizationCode
{
    /// <summary>SHA-256 of the code, hex encoded.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public Guid CompanyId { get; set; }

    public Guid? ApiKeyId { get; set; }

    public string RedirectUri { get; set; } = string.Empty;

    public string CodeChallenge { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public string? Resource { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public bool Used { get; set; }
}

internal enum OAuthTokenKind
{
    Access = 0,
    Refresh = 1,
}

internal sealed class OAuthToken
{
    /// <summary>SHA-256 of the token, hex encoded.</summary>
    public string Hash { get; set; } = string.Empty;

    public OAuthTokenKind Kind { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? ApiKeyId { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public bool Revoked { get; set; }
}
