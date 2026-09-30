namespace MCPal.Cloud.Tenancy;

internal sealed record ValidatedAccessToken(Guid CompanyId, Guid? ApiKeyId);

/// <summary>Validates opaque OAuth access tokens issued by the built-in authorization server.</summary>
internal interface IAccessTokenValidator
{
    Task<ValidatedAccessToken?> ValidateAsync(string accessToken, CancellationToken cancellationToken);
}
