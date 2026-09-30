namespace MCPal.Cloud.Tenancy;

/// <param name="Purpose">Purpose of the key the token was issued from; null for tokens without a key.</param>
internal sealed record ValidatedAccessToken(Guid CompanyId, Guid? ApiKeyId, string ClientId, ApiKeyPurpose? Purpose, IReadOnlyList<string>? AllowedServers);

/// <summary>Validates opaque OAuth access tokens issued by the built-in authorization server.</summary>
internal interface IAccessTokenValidator
{
    Task<ValidatedAccessToken?> ValidateAsync(string accessToken, CancellationToken cancellationToken);
}
