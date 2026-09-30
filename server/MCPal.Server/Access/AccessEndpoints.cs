using System.Security.Claims;
using MCPal.Server.Portal;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tunnel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.Access;

internal sealed record GroupNameRequest(string? Name);

internal sealed record MembersRequest(IReadOnlyList<string>? UserIds);

internal sealed record GrantRequest(string? ServerPattern, IReadOnlyList<string>? ToolPatterns);

internal sealed record GrantResponse(Guid Id, string ServerPattern, IReadOnlyList<string> ToolPatterns);

internal sealed record GroupResponse(Guid Id, string Name, bool IsEveryone, string? ExternalId, IReadOnlyList<string> MemberIds, IReadOnlyList<GrantResponse> Grants);

/// <param name="AllTools">True for owners: they may use every tool regardless of grants.</param>
internal sealed record UserAccessResponse(string UserId, string Email, string Role, bool Disabled, bool AllTools, IReadOnlyList<string> Groups, IReadOnlyList<VisibleTool> Tools);

/// <summary>Groups, grants and the effective access of users. Managing them is for owners; every user can see their own access.</summary>
internal static class AccessEndpoints
{
    public static void Map(RouteGroupBuilder secured)
    {
        ArgumentNullException.ThrowIfNull(secured);

        secured.MapGet("access/me", MyAccessAsync);

        var owners = secured.MapGroup(string.Empty).AddEndpointFilter<OwnerOnlyFilter>();
        owners.MapGet("groups", ListGroupsAsync);
        owners.MapPost("groups", CreateGroupAsync);
        owners.MapPatch("groups/{id:guid}", RenameGroupAsync);
        owners.MapDelete("groups/{id:guid}", DeleteGroupAsync);
        owners.MapPut("groups/{id:guid}/members", SetMembersAsync);
        owners.MapPost("groups/{id:guid}/grants", AddGrantAsync);
        owners.MapPut("grants/{id:guid}", UpdateGrantAsync);
        owners.MapDelete("grants/{id:guid}", DeleteGrantAsync);
        owners.MapGet("access/users/{id}", UserAccessAsync);
    }

    private static async Task<IResult> ListGroupsAsync(ClaimsPrincipal principal, UserManager<PortalUser> users, AccessService access, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        return Results.Json((await access.ListGroupsAsync(companyId, cancellationToken)).Select(ToResponse));
    }

    private static async Task<IResult> CreateGroupAsync(GroupNameRequest? request, ClaimsPrincipal principal, UserManager<PortalUser> users, AccessService access, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var result = await access.CreateGroupAsync(companyId, request?.Name, cancellationToken);
        return result.Value is { } group ? Results.Json(ToResponse(group), statusCode: 201) : Failure(result.Failure, result.Error);
    }

    private static async Task<IResult> RenameGroupAsync(Guid id, GroupNameRequest? request, ClaimsPrincipal principal, UserManager<PortalUser> users, AccessService access, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var result = await access.RenameGroupAsync(companyId, id, request?.Name, cancellationToken);
        return result.Value is { } group ? Results.Json(ToResponse(group)) : Failure(result.Failure, result.Error);
    }

    private static async Task<IResult> DeleteGroupAsync(Guid id, ClaimsPrincipal principal, UserManager<PortalUser> users, AccessService access, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var result = await access.DeleteGroupAsync(companyId, id, cancellationToken);
        return result.Succeeded ? Results.NoContent() : Failure(result.Failure, result.Error);
    }

    private static async Task<IResult> SetMembersAsync(Guid id, MembersRequest? request, ClaimsPrincipal principal, UserManager<PortalUser> users, AccessService access, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var result = await access.SetMembersAsync(companyId, id, request?.UserIds, cancellationToken);
        return result.Value is { } group ? Results.Json(ToResponse(group)) : Failure(result.Failure, result.Error);
    }

    private static async Task<IResult> AddGrantAsync(Guid id, GrantRequest? request, ClaimsPrincipal principal, UserManager<PortalUser> users, AccessService access, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var result = await access.AddGrantAsync(companyId, id, request?.ServerPattern, request?.ToolPatterns, cancellationToken);
        return result.Value is { } grant ? Results.Json(ToResponse(grant), statusCode: 201) : Failure(result.Failure, result.Error);
    }

    private static async Task<IResult> UpdateGrantAsync(Guid id, GrantRequest? request, ClaimsPrincipal principal, UserManager<PortalUser> users, AccessService access, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var result = await access.UpdateGrantAsync(companyId, id, request?.ServerPattern, request?.ToolPatterns, cancellationToken);
        return result.Value is { } grant ? Results.Json(ToResponse(grant)) : Failure(result.Failure, result.Error);
    }

    private static async Task<IResult> DeleteGrantAsync(Guid id, ClaimsPrincipal principal, UserManager<PortalUser> users, AccessService access, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var result = await access.DeleteGrantAsync(companyId, id, cancellationToken);
        return result.Succeeded ? Results.NoContent() : Failure(result.Failure, result.Error);
    }

    private static async Task<IResult> MyAccessAsync(ClaimsPrincipal principal, UserManager<PortalUser> users, AccessEvaluator evaluator, ConnectionRegistry registry, CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return Results.Unauthorized();
        }

        return Results.Json(await BuildAsync(user, evaluator, registry, cancellationToken));
    }

    private static async Task<IResult> UserAccessAsync(string id, ClaimsPrincipal principal, UserManager<PortalUser> users, MCPalDbContext db, AccessEvaluator evaluator, ConnectionRegistry registry, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.CompanyId == companyId, cancellationToken);
        return target is null ? Results.NotFound() : Results.Json(await BuildAsync(target, evaluator, registry, cancellationToken));
    }

    private static async Task<UserAccessResponse> BuildAsync(PortalUser user, AccessEvaluator evaluator, ConnectionRegistry registry, CancellationToken cancellationToken)
    {
        var policy = await evaluator.GetUserPolicyAsync(user.CompanyId, user.Id, cancellationToken);
        // A disabled user has no policy: they may use nothing.
        return policy is null
            ? new UserAccessResponse(user.Id, user.Email ?? string.Empty, TeamEndpoints.RoleName(user.Role), user.Disabled, false, [], [])
            : new UserAccessResponse(user.Id, policy.Email, TeamEndpoints.RoleName(policy.Role), false, policy.IsOwner, policy.GroupNames, AccessService.VisibleTools(registry, user.CompanyId, policy));
    }

    private static IResult Failure(AccessFailure failure, string? error) =>
        failure == AccessFailure.NotFound ? Results.NotFound() : PortalEndpoints.Problems([error ?? "Invalid request."]);

    private static GroupResponse ToResponse(GroupDetails group) =>
        new(group.Id, group.Name, group.IsEveryone, group.ExternalId, group.MemberIds, [.. group.Grants.Select(ToResponse)]);

    private static GrantResponse ToResponse(GrantDetails grant) => new(grant.Id, grant.ServerPattern, grant.ToolPatterns);
}
