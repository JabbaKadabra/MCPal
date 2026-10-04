namespace MCPal.Server.Access.UserContext;

/// <summary>
/// A key that signs caller tokens. Global (shared by all companies): the audience of a token names the company and server, the
/// signature only proves the token came from this MCPal server. The private key is stored protected by Data Protection.
/// </summary>
internal sealed class SigningKey
{
    public const string EllipticCurveAlgorithm = "ES256";

    /// <summary>Key id (<c>kid</c>): the RFC 7638 thumbprint of the public key.</summary>
    public string Kid { get; set; } = string.Empty;

    public string Algorithm { get; set; } = EllipticCurveAlgorithm;

    /// <summary>The public key as a JWK (RFC 7517), exactly as the JWKS endpoint serves it.</summary>
    public string PublicJwkJson { get; set; } = string.Empty;

    /// <summary>The PKCS#8 private key, protected with Data Protection (base64). Unreadable without the key ring.</summary>
    public string ProtectedPrivateKey { get; set; } = string.Empty;

    /// <summary>When the key was created. A successor is published (listed in the JWKS) from here, before it signs anything.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The key signs tokens from this moment.</summary>
    public DateTimeOffset ActivatesAt { get; set; }

    /// <summary>The key stops signing at this moment but stays in the JWKS until <see cref="RemoveAt"/>.</summary>
    public DateTimeOffset RetiresAt { get; set; }

    /// <summary>The key leaves the JWKS (and the database) at this moment.</summary>
    public DateTimeOffset RemoveAt { get; set; }
}
