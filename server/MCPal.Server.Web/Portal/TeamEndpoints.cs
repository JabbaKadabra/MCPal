using System.Security.Claims;
using MCPal.Server.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace MCPal.Server.Portal;

internal sealed record InviteRequest(string? Email, string? Role);

internal sealed record AcceptInvitationRequest(string? Token, string? Password);

internal sealed record TeamMemberResponse(string Id, string Email, string? DisplayName, string Role, bool EmailConfirmed, bool Disabled, IReadOnlyList<string> Groups);

internal sealed record InvitationResponse(Guid Id, string Email, string Role, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, string? InvitedBy);

internal sealed record TeamResponse(IReadOnlyList<TeamMemberResponse> Users, IReadOnlyList<InvitationResponse> Invitations);

internal sealed record InvitationPreviewResponse(string CompanyName, string Email, string Role);

/// <summary>Users and invitations. Everything but accepting and previewing an invitation is for owners only.</summary>
internal static class TeamEndpoints
{
    public static void Map(RouteGroupBuilder portal, RouteGroupBuilder secured)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(secured);

        // Anonymous: the invitation token is the credential. Rate limit and anti-forgery come from the portal group.
        portal.MapGet("invitations/preview", PreviewAsync);
        portal.MapPost("invitations/accept", AcceptAsync);

        var owners = secured.MapGroup(string.Empty).AddEndpointFilter<OwnerOnlyFilter>();
        owners.MapGet("users", ListAsync);
        owners.MapPost("users", InviteAsync);
        owners.MapDelete("users/{id}", RemoveAsync);
        owners.MapPost("users/{id}/disable", (string id, ClaimsPrincipal principal, UserManager<PortalUser> users, TeamService team, CancellationToken cancellationToken) =>
            SetDisabledAsync(id, disabled: true, principal, users, team, cancellationToken));
        owners.MapPost("users/{id}/enable", (string id, ClaimsPrincipal principal, UserManager<PortalUser> users, TeamService team, CancellationToken cancellationToken) =>
            SetDisabledAsync(id, disabled: false, principal, users, team, cancellationToken));
        owners.MapDelete("invitations/{id:guid}", CancelAsync);
    }

    internal static string RoleName(PortalRole role) => role.ToString().ToLowerInvariant();

    private static async Task<IResult> ListAsync(ClaimsPrincipal principal, UserManager<PortalUser> users, TeamService team, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var (members, invitations) = await team.ListAsync(companyId, cancellationToken);
        return Results.Json(new TeamResponse(
            [.. members.Select(m => new TeamMemberResponse(m.Id, m.Email, m.DisplayName, RoleName(m.Role), m.EmailConfirmed, m.Disabled, m.Groups))],
            [.. invitations.Select(ToResponse)]));
    }

    private static async Task<IResult> InviteAsync(InviteRequest? request, ClaimsPrincipal principal, UserManager<PortalUser> users, TeamService team, CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(principal) is not { } inviter)
        {
            return Results.Unauthorized();
        }

        var role = Enum.TryParse<PortalRole>(request?.Role, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : (PortalRole?)null;
        var result = await team.InviteAsync(inviter, request?.Email, role, cancellationToken);
        return result.Value is { } invitation
            ? Results.Json(ToResponse(invitation), statusCode: 201)
            : PortalEndpoints.Problems(result.Errors);
    }

    private static async Task<IResult> RemoveAsync(string id, ClaimsPrincipal principal, UserManager<PortalUser> users, TeamService team, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var result = await team.RemoveUserAsync(companyId, id, cancellationToken);
        return result.Failure switch
        {
            TeamFailure.None => Results.NoContent(),
            TeamFailure.NotFound => Results.NotFound(),
            _ => PortalEndpoints.Problems(result.Errors),
        };
    }

    private static async Task<IResult> SetDisabledAsync(string id, bool disabled, ClaimsPrincipal principal, UserManager<PortalUser> users, TeamService team, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var result = await team.SetDisabledAsync(companyId, id, disabled, cancellationToken);
        return result.Failure switch
        {
            TeamFailure.None when result.Value is { } member => Results.Json(new TeamMemberResponse(member.Id, member.Email, member.DisplayName, RoleName(member.Role), member.EmailConfirmed, member.Disabled, member.Groups)),
            TeamFailure.NotFound => Results.NotFound(),
            _ => PortalEndpoints.Problems(result.Errors),
        };
    }

    private static async Task<IResult> CancelAsync(Guid id, ClaimsPrincipal principal, UserManager<PortalUser> users, TeamService team, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        return await team.CancelInvitationAsync(companyId, id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> PreviewAsync(string? token, TeamService team, CancellationToken cancellationToken)
    {
        return await team.PreviewAsync(token, cancellationToken) is { } preview
            ? Results.Json(new InvitationPreviewResponse(preview.CompanyName, preview.Email, RoleName(preview.Role)))
            : PortalEndpoints.Problems([TeamService.InvalidInvitation]);
    }

    private static async Task<IResult> AcceptAsync(
        AcceptInvitationRequest? request,
        TeamService team,
        SignInManager<PortalUser> signIn,
        ICompanyService companies,
        CancellationToken cancellationToken)
    {
        var result = await team.AcceptAsync(request?.Token, request?.Password, cancellationToken);
        if (result.Value is not { } user)
        {
            return PortalEndpoints.Problems(result.Errors);
        }

        await signIn.SignInAsync(user, isPersistent: true);
        var company = await companies.FindAsync(user.CompanyId, cancellationToken);
        return Results.Json(new MeResponse(user.Email ?? string.Empty, user.CompanyId, company?.Name ?? string.Empty, RoleName(user.Role), user.DisplayName), statusCode: 201);
    }

    private static InvitationResponse ToResponse(PendingInvitation i) => new(i.Id, i.Email, RoleName(i.Role), i.ExpiresAt, i.CreatedAt, i.InvitedBy);
}

/// <summary>Answers 403 unless the signed-in user is an owner. The role is read from the database on every call, so a removed or demoted user loses access at once.</summary>
internal sealed class OwnerOnlyFilter(UserManager<PortalUser> users) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var user = await users.GetUserAsync(context.HttpContext.User);
        return user is { Role: PortalRole.Owner }
            ? await next(context)
            : user is null
                ? Results.Unauthorized()
                : PortalEndpoints.Problems(["Only owners can do this."], 403);
    }
}
