using System.Text;
using System.Security.Cryptography;
using MCPal.Server.Access;
using MCPal.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.Tenancy;

internal interface ICompanyService
{
    Task<Company> CreateAsync(string name, CancellationToken cancellationToken);

    Task<Company?> FindAsync(Guid companyId, CancellationToken cancellationToken);
}

internal sealed class CompanyService(MCPalDbContext db, TimeProvider timeProvider) : ICompanyService
{
    public async Task<Company> CreateAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var baseSlug = Slugify(name);
        var slug = baseSlug;
        while (await db.Companies.AnyAsync(c => c.Slug == slug, cancellationToken))
        {
            slug = $"{baseSlug}-{RandomNumberGenerator.GetHexString(4, lowercase: true)}";
        }

        var company = new Company { Id = Guid.NewGuid(), Name = name.Trim(), Slug = slug, CreatedAt = timeProvider.GetUtcNow() };
        db.Companies.Add(company);

        // A new company keeps the 15-minute onboarding: the implicit Everyone group may use every tool until an owner narrows it.
        var everyone = new AccessGroup { Id = Guid.NewGuid(), CompanyId = company.Id, Name = AccessGroup.EveryoneName, IsEveryone = true, CreatedAt = company.CreatedAt };
        db.AccessGroups.Add(everyone);
        db.AccessGrants.Add(new AccessGrant { Id = Guid.NewGuid(), GroupId = everyone.Id, CompanyId = company.Id, ServerPattern = "*", ToolPatterns = ["*"] });
        await db.SaveChangesAsync(cancellationToken);
        return company;
    }

    public async Task<Company?> FindAsync(Guid companyId, CancellationToken cancellationToken)
    {
        return await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, cancellationToken);
    }

    internal static string Slugify(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "company" : slug[..Math.Min(slug.Length, 60)];
    }
}
