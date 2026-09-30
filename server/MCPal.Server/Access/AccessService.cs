using MCPal.Contracts;
using MCPal.Server.Storage;
using MCPal.Server.Tunnel;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.Access;

internal sealed record GrantDetails(Guid Id, Guid GroupId, string ServerPattern, IReadOnlyList<string> ToolPatterns);

/// <param name="MemberIds">Explicit members. Empty for <c>Everyone</c>, whose members are all active users of the company.</param>
internal sealed record GroupDetails(Guid Id, string Name, bool IsEveryone, string? ExternalId, IReadOnlyList<string> MemberIds, IReadOnlyList<GrantDetails> Grants);

internal enum AccessFailure
{
    None,
    Invalid,
    NotFound,
}

internal sealed record AccessResult<T>(T? Value, string? Error, AccessFailure Failure = AccessFailure.None)
    where T : class
{
    public bool Succeeded => Failure == AccessFailure.None;

    public static AccessResult<T> Ok(T value) => new(value, null);

    public static AccessResult<T> Fail(AccessFailure failure, string? error = null) => new(null, error, failure);
}

/// <summary>
/// Groups and grants of one company. Every method takes the company from the caller's principal and filters by it, so one
/// company can neither see nor change another's groups. Every write invalidates the cached policy of the company.
/// </summary>
internal sealed class AccessService(MCPalDbContext db, AccessPolicyCache cache, TimeProvider timeProvider)
{
    public const int MaxGroupNameLength = 100;
    public const int MaxToolPatterns = 50;

    public async Task<IReadOnlyList<GroupDetails>> ListGroupsAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var groups = await db.AccessGroups.AsNoTracking().Where(g => g.CompanyId == companyId).OrderByDescending(g => g.IsEveryone).ThenBy(g => g.Name).ToListAsync(cancellationToken);
        var members = await db.AccessGroupMembers.AsNoTracking().Where(m => m.CompanyId == companyId).ToListAsync(cancellationToken);
        var grants = await db.AccessGrants.AsNoTracking().Where(g => g.CompanyId == companyId).OrderBy(g => g.ServerPattern).ToListAsync(cancellationToken);
        return [.. groups.Select(group => Details(group, members, grants))];
    }

    public async Task<AccessResult<GroupDetails>> CreateGroupAsync(Guid companyId, string? name, CancellationToken cancellationToken)
    {
        if (ValidateName(name) is { } problem)
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.Invalid, problem);
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (await NameTakenAsync(companyId, trimmed, exceptGroupId: null, cancellationToken))
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.Invalid, NameTakenMessage);
        }

        var group = new AccessGroup { Id = Guid.NewGuid(), CompanyId = companyId, Name = trimmed, CreatedAt = timeProvider.GetUtcNow() };
        db.AccessGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate(companyId);
        return AccessResult<GroupDetails>.Ok(new GroupDetails(group.Id, group.Name, false, null, [], []));
    }

    public async Task<AccessResult<GroupDetails>> RenameGroupAsync(Guid companyId, Guid groupId, string? name, CancellationToken cancellationToken)
    {
        var group = await db.AccessGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId, cancellationToken);
        if (group is null)
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.NotFound);
        }

        if (group.IsEveryone)
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.Invalid, EveryoneFixedMessage);
        }

        if (ValidateName(name) is { } problem)
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.Invalid, problem);
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (await NameTakenAsync(companyId, trimmed, groupId, cancellationToken))
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.Invalid, NameTakenMessage);
        }

        group.Name = trimmed;
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate(companyId);
        return AccessResult<GroupDetails>.Ok(await DetailsAsync(group, cancellationToken));
    }

    public async Task<AccessResult<GroupDetails>> DeleteGroupAsync(Guid companyId, Guid groupId, CancellationToken cancellationToken)
    {
        var group = await db.AccessGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId, cancellationToken);
        if (group is null)
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.NotFound);
        }

        if (group.IsEveryone)
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.Invalid, EveryoneFixedMessage);
        }

        var details = await DetailsAsync(group, cancellationToken);
        db.AccessGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate(companyId);
        return AccessResult<GroupDetails>.Ok(details);
    }

    /// <summary>Replaces the members of a group. <c>Everyone</c> has no member list: all active users belong to it.</summary>
    public async Task<AccessResult<GroupDetails>> SetMembersAsync(Guid companyId, Guid groupId, IReadOnlyCollection<string>? userIds, CancellationToken cancellationToken)
    {
        var group = await db.AccessGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId, cancellationToken);
        if (group is null)
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.NotFound);
        }

        if (group.IsEveryone)
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.Invalid, "Everyone contains all users of the company and has no member list.");
        }

        var wanted = (userIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        var known = await db.Users.AsNoTracking().Where(u => u.CompanyId == companyId && wanted.Contains(u.Id)).Select(u => u.Id).ToListAsync(cancellationToken);
        if (known.Count != wanted.Count)
        {
            return AccessResult<GroupDetails>.Fail(AccessFailure.Invalid, "At least one of the users does not exist in this company.");
        }

        var current = await db.AccessGroupMembers.Where(m => m.GroupId == groupId && m.CompanyId == companyId).ToListAsync(cancellationToken);
        db.AccessGroupMembers.RemoveRange(current.Where(m => !wanted.Contains(m.UserId)));
        var existing = current.Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
        db.AccessGroupMembers.AddRange(wanted.Where(id => !existing.Contains(id)).Select(id => new AccessGroupMember { GroupId = groupId, UserId = id, CompanyId = companyId }));
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate(companyId);
        return AccessResult<GroupDetails>.Ok(await DetailsAsync(group, cancellationToken));
    }

    public async Task<AccessResult<GrantDetails>> AddGrantAsync(Guid companyId, Guid groupId, string? serverPattern, IReadOnlyList<string>? toolPatterns, CancellationToken cancellationToken)
    {
        if (!await db.AccessGroups.AsNoTracking().AnyAsync(g => g.Id == groupId && g.CompanyId == companyId, cancellationToken))
        {
            return AccessResult<GrantDetails>.Fail(AccessFailure.NotFound);
        }

        if (ValidateGrant(serverPattern, toolPatterns) is { } problem)
        {
            return AccessResult<GrantDetails>.Fail(AccessFailure.Invalid, problem);
        }

        var grant = new AccessGrant
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            CompanyId = companyId,
            ServerPattern = serverPattern?.Trim() ?? string.Empty,
            ToolPatterns = Normalize(toolPatterns),
        };
        db.AccessGrants.Add(grant);
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate(companyId);
        return AccessResult<GrantDetails>.Ok(ToDetails(grant));
    }

    public async Task<AccessResult<GrantDetails>> UpdateGrantAsync(Guid companyId, Guid grantId, string? serverPattern, IReadOnlyList<string>? toolPatterns, CancellationToken cancellationToken)
    {
        var grant = await db.AccessGrants.FirstOrDefaultAsync(g => g.Id == grantId && g.CompanyId == companyId, cancellationToken);
        if (grant is null)
        {
            return AccessResult<GrantDetails>.Fail(AccessFailure.NotFound);
        }

        if (ValidateGrant(serverPattern, toolPatterns) is { } problem)
        {
            return AccessResult<GrantDetails>.Fail(AccessFailure.Invalid, problem);
        }

        grant.ServerPattern = serverPattern?.Trim() ?? string.Empty;
        grant.ToolPatterns = Normalize(toolPatterns);
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate(companyId);
        return AccessResult<GrantDetails>.Ok(ToDetails(grant));
    }

    public async Task<AccessResult<GrantDetails>> DeleteGrantAsync(Guid companyId, Guid grantId, CancellationToken cancellationToken)
    {
        var grant = await db.AccessGrants.FirstOrDefaultAsync(g => g.Id == grantId && g.CompanyId == companyId, cancellationToken);
        if (grant is null)
        {
            return AccessResult<GrantDetails>.Fail(AccessFailure.NotFound);
        }

        db.AccessGrants.Remove(grant);
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate(companyId);
        return AccessResult<GrantDetails>.Ok(ToDetails(grant));
    }

    /// <summary>The tools of the company that are online right now and that the policy allows.</summary>
    public static IReadOnlyList<VisibleTool> VisibleTools(ConnectionRegistry registry, Guid companyId, UserPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policy);

        return [.. registry.Tools(companyId)
            .Where(tool => policy.Allows(tool.ServerName, tool.Descriptor.Name))
            .OrderBy(tool => tool.ServerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tool => tool.Descriptor.Name, StringComparer.Ordinal)
            .Select(tool => new VisibleTool(tool.ServerName, tool.Descriptor.Name, tool.PublicName))];
    }

    private const string NameTakenMessage = "A group with this name already exists.";
    private const string EveryoneFixedMessage = "The Everyone group cannot be renamed or deleted. Change its grants instead.";

    private static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "A group name is required.";
        }

        return trimmed.Length > MaxGroupNameLength || trimmed.Any(char.IsControl)
            ? $"The group name can have at most {MaxGroupNameLength} characters and no control characters."
            : null;
    }

    private static string? ValidateGrant(string? serverPattern, IReadOnlyList<string>? toolPatterns)
    {
        var server = serverPattern?.Trim();
        if (!ToolNaming.IsValidServerName(server))
        {
            return "The server pattern is empty, longer than 200 characters or contains control characters.";
        }

        if (toolPatterns is not { Count: > 0 })
        {
            return "At least one tool pattern is required. Use * for all tools.";
        }

        if (toolPatterns.Count > MaxToolPatterns)
        {
            return $"At most {MaxToolPatterns} tool patterns are allowed.";
        }

        var invalid = toolPatterns.FirstOrDefault(pattern => !ToolPattern.IsValid(pattern));
        return invalid is not null
            ? $"'{invalid}' is not a valid tool pattern. Use letters, digits, _ - . and the wildcards * and ?."
            : null;
    }

    private static string[] Normalize(IReadOnlyList<string>? toolPatterns) => [.. (toolPatterns ?? []).Distinct(StringComparer.Ordinal)];

    private async Task<bool> NameTakenAsync(Guid companyId, string name, Guid? exceptGroupId, CancellationToken cancellationToken)
    {
        // The names of one company are few, so the case-insensitive comparison happens in memory (no provider specific translation).
        var names = await db.AccessGroups.AsNoTracking().Where(g => g.CompanyId == companyId && g.Id != exceptGroupId).Select(g => g.Name).ToListAsync(cancellationToken);
        return names.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<GroupDetails> DetailsAsync(AccessGroup group, CancellationToken cancellationToken)
    {
        var members = await db.AccessGroupMembers.AsNoTracking().Where(m => m.GroupId == group.Id && m.CompanyId == group.CompanyId).ToListAsync(cancellationToken);
        var grants = await db.AccessGrants.AsNoTracking().Where(g => g.GroupId == group.Id && g.CompanyId == group.CompanyId).OrderBy(g => g.ServerPattern).ToListAsync(cancellationToken);
        return Details(group, members, grants);
    }

    private static GroupDetails Details(AccessGroup group, IEnumerable<AccessGroupMember> members, IEnumerable<AccessGrant> grants) => new(
        group.Id,
        group.Name,
        group.IsEveryone,
        group.ExternalId,
        [.. members.Where(m => m.GroupId == group.Id && !group.IsEveryone).Select(m => m.UserId)],
        [.. grants.Where(g => g.GroupId == group.Id).Select(ToDetails)]);

    private static GrantDetails ToDetails(AccessGrant grant) => new(grant.Id, grant.GroupId, grant.ServerPattern, grant.ToolPatterns);
}

/// <summary>A tool that is online and allowed.</summary>
internal sealed record VisibleTool(string Server, string Tool, string PublicName);
