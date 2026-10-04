namespace MCPal.Server.Access;

/// <summary>Answers "what may this user use?" from the cached company policy, evaluated on every request.</summary>
internal sealed class AccessEvaluator(AccessPolicyCache cache, AccessPolicyLoader loader)
{
    public async Task<CompanyPolicy> GetCompanyPolicyAsync(Guid companyId, CancellationToken cancellationToken) =>
        await cache.GetAsync(companyId, ct => loader.LoadAsync(companyId, ct), cancellationToken);

    /// <summary>The policy of the user, or null when the user does not exist, is disabled or belongs to another company.</summary>
    public async Task<UserPolicy?> GetUserPolicyAsync(Guid companyId, string userId, CancellationToken cancellationToken) =>
        (await GetCompanyPolicyAsync(companyId, cancellationToken)).ForUser(userId);
}
