using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using MCPal.Cloud.Storage;
using MCPal.Cloud.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Cloud.Portal;

internal sealed record TeamMember(string Id, string Email, PortalRole Role, bool EmailConfirmed);

internal sealed record PendingInvitation(Guid Id, string Email, PortalRole Role, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, string? InvitedBy);

internal sealed record InvitationPreview(string CompanyName, string Email, PortalRole Role);

/// <summary>Outcome of a team operation: either a value or messages for the caller.</summary>
internal sealed record TeamResult<T>(T? Value, IReadOnlyList<string> Errors, TeamFailure Failure = TeamFailure.None)
    where T : class
{
    public bool Succeeded => Failure == TeamFailure.None;

    public static TeamResult<T> Ok(T value) => new(value, []);

    public static TeamResult<T> Fail(TeamFailure failure, params string[] errors) => new(null, errors, failure);
}

internal enum TeamFailure
{
    None,
    Invalid,
    NotFound,
}

/// <summary>
/// Users and invitations of one company. Every method takes the company from the caller's principal (never from a request)
/// and filters by it, so one company can neither see nor change another's team.
/// </summary>
internal sealed class TeamService(
    MCPalDbContext db,
    UserManager<PortalUser> users,
    IApiKeyService apiKeys,
    ICompanyService companies,
    AccountMailer mailer,
    TimeProvider timeProvider)
{
    public const string InvalidInvitation = "This invitation is invalid or has expired.";
    public const string CannotInvite = "This email address cannot be invited.";

    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public async Task<(IReadOnlyList<TeamMember> Users, IReadOnlyList<PendingInvitation> Invitations)> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var members = await db.Users.AsNoTracking()
            .Where(u => u.CompanyId == companyId)
            .OrderBy(u => u.Email)
            .Select(u => new TeamMember(u.Id, u.Email ?? string.Empty, u.Role, u.EmailConfirmed))
            .ToListAsync(cancellationToken);
        var emails = members.ToDictionary(m => m.Id, m => m.Email);
        var invitations = await db.Invitations.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.AcceptedAt == null && i.ExpiresAt > now)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(cancellationToken);
        return (members, [.. invitations.Select(i => new PendingInvitation(i.Id, i.Email, i.Role, i.ExpiresAt, i.CreatedAt, emails.GetValueOrDefault(i.CreatedByUserId)))]);
    }

    public async Task<TeamResult<PendingInvitation>> InviteAsync(PortalUser inviter, string? email, PortalRole? role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inviter);

        if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
        {
            return TeamResult<PendingInvitation>.Fail(TeamFailure.Invalid, "A valid email address is required.");
        }

        if (role is null)
        {
            return TeamResult<PendingInvitation>.Fail(TeamFailure.Invalid, "The role must be 'owner' or 'member'.");
        }

        email = email.Trim();

        // An address can have one account, in one company. The message is the same whichever company holds it.
        if (await users.FindByEmailAsync(email) is not null)
        {
            return TeamResult<PendingInvitation>.Fail(TeamFailure.Invalid, CannotInvite);
        }

        var company = await companies.FindAsync(inviter.CompanyId, cancellationToken);
        if (company is null)
        {
            return TeamResult<PendingInvitation>.Fail(TeamFailure.NotFound);
        }

        var now = timeProvider.GetUtcNow();
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            CompanyId = inviter.CompanyId,
            Email = email,
            Role = role.Value,
            TokenHash = ApiKeyService.Hash(token),
            CreatedAt = now,
            ExpiresAt = now.Add(InvitationLifetime),
            CreatedByUserId = inviter.Id,
        };

        // A new invitation for the same address replaces the pending one, so only the newest link works.
        var pending = await db.Invitations.Where(i => i.CompanyId == inviter.CompanyId && i.AcceptedAt == null).ToListAsync(cancellationToken);
        db.Invitations.RemoveRange(pending.Where(i => string.Equals(i.Email, email, StringComparison.OrdinalIgnoreCase)));
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
        await mailer.SendInvitationAsync(email, company.Name, token, cancellationToken);
        return TeamResult<PendingInvitation>.Ok(new PendingInvitation(invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, invitation.CreatedAt, inviter.Email));
    }

    public async Task<InvitationPreview?> PreviewAsync(string? token, CancellationToken cancellationToken)
    {
        if (await FindOpenInvitationAsync(token, cancellationToken) is not { } invitation
            || await companies.FindAsync(invitation.CompanyId, cancellationToken) is not { Disabled: false } company)
        {
            return null;
        }

        return new InvitationPreview(company.Name, invitation.Email, invitation.Role);
    }

    /// <summary>Creates the user of an invitation. The password is checked before the invitation is used up, so a weak password can be retried.</summary>
    public async Task<TeamResult<PortalUser>> AcceptAsync(string? token, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
        {
            return TeamResult<PortalUser>.Fail(TeamFailure.Invalid, "A password is required.");
        }

        if (await FindOpenInvitationAsync(token, cancellationToken) is not { } invitation
            || await companies.FindAsync(invitation.CompanyId, cancellationToken) is not { Disabled: false }
            || await users.FindByEmailAsync(invitation.Email) is not null)
        {
            return TeamResult<PortalUser>.Fail(TeamFailure.Invalid, InvalidInvitation);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await db.Invitations
            .Where(i => i.Id == invitation.Id && i.AcceptedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAt, timeProvider.GetUtcNow()), cancellationToken);
        if (claimed == 0)
        {
            return TeamResult<PortalUser>.Fail(TeamFailure.Invalid, InvalidInvitation);
        }

        // The invitation went to this mailbox, so the address counts as confirmed.
        var user = new PortalUser
        {
            UserName = invitation.Email,
            Email = invitation.Email,
            EmailConfirmed = true,
            CompanyId = invitation.CompanyId,
            Role = invitation.Role,
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TeamResult<PortalUser>.Fail(TeamFailure.Invalid, [.. created.Errors.Select(e => e.Description)]);
        }

        await transaction.CommitAsync(cancellationToken);
        return TeamResult<PortalUser>.Ok(user);
    }

    public async Task<bool> CancelInvitationAsync(Guid companyId, Guid invitationId, CancellationToken cancellationToken)
    {
        var deleted = await db.Invitations
            .Where(i => i.Id == invitationId && i.CompanyId == companyId && i.AcceptedAt == null)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    /// <summary>
    /// Removes a user of the company. The Claude keys they created stop working; keys for agents stay, because running agents use them.
    /// The last owner cannot be removed.
    /// </summary>
    public async Task<TeamResult<TeamMember>> RemoveUserAsync(Guid companyId, string userId, CancellationToken cancellationToken)
    {
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.CompanyId == companyId, cancellationToken);
        if (target is null)
        {
            return TeamResult<TeamMember>.Fail(TeamFailure.NotFound);
        }

        if (target.Role == PortalRole.Owner && await db.Users.CountAsync(u => u.CompanyId == companyId && u.Role == PortalRole.Owner, cancellationToken) <= 1)
        {
            return TeamResult<TeamMember>.Fail(TeamFailure.Invalid, "The last owner cannot be removed. Make someone else an owner first.");
        }

        var personalKeys = await db.ApiKeys.AsNoTracking()
            .Where(k => k.CompanyId == companyId && k.CreatedByUserId == userId && k.Purpose == ApiKeyPurpose.Client && !k.Disabled)
            .Select(k => k.Id)
            .ToListAsync(cancellationToken);
        foreach (var keyId in personalKeys)
        {
            await apiKeys.RevokeAsync(companyId, keyId, cancellationToken);
        }

        var removed = new TeamMember(target.Id, target.Email ?? string.Empty, target.Role, target.EmailConfirmed);
        var result = await users.DeleteAsync(target);
        return result.Succeeded
            ? TeamResult<TeamMember>.Ok(removed)
            : TeamResult<TeamMember>.Fail(TeamFailure.Invalid, [.. result.Errors.Select(e => e.Description)]);
    }

    private async Task<Invitation?> FindOpenInvitationAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = ApiKeyService.Hash(token);
        var now = timeProvider.GetUtcNow();
        return await db.Invitations.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash && i.AcceptedAt == null && i.ExpiresAt > now, cancellationToken);
    }
}
