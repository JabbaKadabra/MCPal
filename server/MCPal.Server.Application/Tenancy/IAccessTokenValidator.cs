namespace MCPal.Server.Tenancy;

/// <summary>An OAuth access token always belongs to a portal user of the company.</summary>
internal sealed record ValidatedAccessToken(Guid CompanyId, string UserId, string ClientId);

/// <summary>Validates opaque OAuth access tokens issued by the built-in authorization server.</summary>
internal interface IAccessTokenValidator
{
    Task<ValidatedAccessToken?> ValidateAsync(string accessToken, CancellationToken cancellationToken);
}
