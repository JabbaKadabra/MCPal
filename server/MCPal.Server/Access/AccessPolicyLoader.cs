using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.Access;

/// <summary>Builds the <see cref="CompanyPolicy"/> of a company from the database: the company's slug and four queries (users, groups, members, grants), each filtered by the company.</summary>
internal sealed class AccessPolicyLoader(MCPalDbContext db)
{
    public async Task<CompanyPolicy> LoadAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var slug = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Slug).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        var users = await db.Users.AsNoTracking()
            .Where(u => u.CompanyId == companyId)
            .Active(db.Companies)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.Role })
            .ToListAsync(cancellationToken);
        var groups = await db.AccessGroups.AsNoTracking().Where(g => g.CompanyId == companyId).ToListAsync(cancellationToken);
        var members = await db.AccessGroupMembers.AsNoTracking().Where(m => m.CompanyId == companyId).ToListAsync(cancellationToken);
        var grants = await db.AccessGrants.AsNoTracking().Where(g => g.CompanyId == companyId).ToListAsync(cancellationToken);

        var rulesByGroup = grants
            .GroupBy(g => g.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(grant => new GrantRule(grant.ServerPattern, grant.ToolPatterns)).ToList());
        var everyone = groups.Where(g => g.IsEveryone).ToList();
        var groupsById = groups.ToDictionary(g => g.Id);
        var membershipsByUser = members.ToLookup(m => m.UserId);

        var policies = new Dictionary<string, UserPolicy>(StringComparer.Ordinal);
        foreach (var user in users)
        {
            var userGroups = everyone
                .Concat(membershipsByUser[user.Id].Select(m => groupsById.GetValueOrDefault(m.GroupId)).OfType<AccessGroup>().Where(g => !g.IsEveryone))
                .DistinctBy(g => g.Id)
                .ToList();
            var rules = userGroups.SelectMany(g => rulesByGroup.GetValueOrDefault(g.Id) ?? []).ToList();
            policies[user.Id] = new UserPolicy(user.Id, user.Email ?? string.Empty, user.DisplayName, user.Role, [.. userGroups.Select(g => g.Name)], rules);
        }

        return new CompanyPolicy(companyId, slug, policies);
    }
}
