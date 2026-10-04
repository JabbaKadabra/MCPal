namespace MCPal.Server.Access;

/// <summary>
/// A set of users with the same tool access. Every company has exactly one <see cref="IsEveryone"/> group: all active users
/// are members implicitly, it cannot be renamed or deleted.
/// </summary>
internal sealed class AccessGroup
{
    public const string EveryoneName = "Everyone";

    public const int MaxNameLength = 100;

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsEveryone { get; set; }

    /// <summary>Identifier of the matching group at an external identity provider (SSO). Reserved for SSO.</summary>
    public string? ExternalId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Membership of a user in a group. <see cref="CompanyId"/> repeats the company so every query can filter by it.</summary>
internal sealed class AccessGroupMember
{
    public Guid GroupId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public Guid CompanyId { get; set; }
}

/// <summary>Allows the tools matching <see cref="ToolPatterns"/> of the servers matching <see cref="ServerPattern"/> to the members of a group.</summary>
internal sealed class AccessGrant
{
    public Guid Id { get; set; }

    public Guid GroupId { get; set; }

    public Guid CompanyId { get; set; }

    /// <summary>Glob over server names, case-insensitive.</summary>
    public string ServerPattern { get; set; } = string.Empty;

    /// <summary>Globs over the tool names of the local server, case-sensitive. <c>*</c> allows all tools.</summary>
    public string[] ToolPatterns { get; set; } = [];
}
