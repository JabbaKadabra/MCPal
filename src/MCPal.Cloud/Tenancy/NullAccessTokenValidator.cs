namespace MCPal.Cloud.Tenancy;

/// <summary>Placeholder until the OAuth authorization server issues tokens.</summary>
internal sealed class NullAccessTokenValidator : IAccessTokenValidator
{
    public Task<ValidatedAccessToken?> ValidateAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult<ValidatedAccessToken?>(null);
}
