using MCPal.Contracts;
using MCPal.Server.Tenancy;

namespace MCPal.Server.Access;

/// <summary>One grant of a group: a server glob (case-insensitive) and tool globs (case-sensitive).</summary>
internal sealed record GrantRule(string ServerPattern, IReadOnlyList<string> ToolPatterns)
{
    public bool Allows(string serverName, string toolName) =>
        ToolPattern.Matches(ServerPattern, serverName, ignoreCase: true)
        && ToolPatterns.Any(pattern => ToolPattern.Matches(pattern, toolName, ignoreCase: false));
}

/// <summary>What one active user of a company may use, with the facts a caller token needs about them.</summary>
/// <param name="GroupNames">Names of the groups the user is in, always including <c>Everyone</c>.</param>
/// <param name="Grants">The union of the grants of those groups.</param>
internal sealed record UserPolicy(string UserId, string Email, string? DisplayName, PortalRole Role, IReadOnlyList<string> GroupNames, IReadOnlyList<GrantRule> Grants)
{
    public bool IsOwner => Role == PortalRole.Owner;

    /// <summary>Owners may use everything. Everyone else needs a grant.</summary>
    public bool Allows(string serverName, string toolName) =>
        IsOwner || Grants.Any(grant => grant.Allows(serverName, toolName));
}

/// <summary>The policies of all active users of one company. A user that is missing (unknown, disabled, other company) may use nothing.</summary>
internal sealed record CompanyPolicy(Guid CompanyId, IReadOnlyDictionary<string, UserPolicy> Users)
{
    public UserPolicy? ForUser(string userId) => Users.GetValueOrDefault(userId);
}
