using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MCPal.Cloud.Storage;
using MCPal.Cloud.Tenancy;
using MCPal.Cloud.Tunnel;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCPal.Cloud.Portal;

internal sealed record SignupRequest(string? CompanyName, string? Email, string? Password);

internal sealed record LoginRequest(string? Email, string? Password);

internal sealed record CreateKeyRequest(string? Name, DateTimeOffset? ExpiresAt);

internal sealed record MeResponse(string Email, Guid CompanyId, string CompanyName);

internal sealed record ApiKeyResponse(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, bool Disabled);

internal sealed record CreatedApiKeyResponse(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string Key);

internal sealed record ServerResponse(string Name, IReadOnlyList<string> Tools);

internal sealed record RejectedServerResponse(string Server, string Reason);

internal sealed record RejectedToolResponse(string Server, string Tool, string Reason);

internal sealed record ConnectionResponse(
    string AgentName,
    DateTimeOffset ConnectedAt,
    string? ApiKeyName,
    IReadOnlyList<ServerResponse> Servers,
    IReadOnlyList<RejectedServerResponse> Rejected,
    IReadOnlyList<RejectedToolResponse> RejectedTools);

internal sealed record ConnectInfoResponse(string McpUrl, string Issuer, string ClaudeCodeCommand, string ClaudeCodeCommandWithHeader);

internal static class PortalEndpoints
{
    public const string Policy = "Portal";
    public const string RateLimitPolicy = "portal";

    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/portal").AddEndpointFilter<AntiforgeryFilter>().RequireRateLimiting(RateLimitPolicy);
        group.MapGet("csrf", Csrf);

        var auth = group.MapGroup("auth");
        auth.MapPost("signup", SignupAsync);
        auth.MapPost("login", LoginAsync);
        auth.MapPost("logout", LogoutAsync);
        auth.MapGet("me", MeAsync).RequireAuthorization(Policy);

        var secured = group.MapGroup(string.Empty).RequireAuthorization(Policy);
        secured.MapGet("keys", ListKeysAsync);
        secured.MapPost("keys", CreateKeyAsync);
        secured.MapDelete("keys/{id:guid}", RevokeKeyAsync);
        secured.MapGet("connections", ConnectionsAsync);
        secured.MapGet("connect-info", ConnectInfo);
    }

    private static IResult Csrf(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Json(new { token = tokens.RequestToken });
    }

    private static async Task<IResult> SignupAsync(
        SignupRequest? request,
        UserManager<PortalUser> users,
        SignInManager<PortalUser> signIn,
        ICompanyService companies,
        MCPalDbContext db,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(request?.CompanyName))
        {
            problems.Add("Company name is required.");
        }

        if (string.IsNullOrWhiteSpace(request?.Email) || !new EmailAddressAttribute().IsValid(request.Email))
        {
            problems.Add("A valid email address is required.");
        }

        if (problems.Count > 0 || request?.CompanyName is null || request.Email is null || request.Password is null)
        {
            return Problems(problems.Count > 0 ? problems : ["Company name, email and password are required."]);
        }

        if (await users.FindByEmailAsync(request.Email) is not null)
        {
            return Problems(["An account with this email already exists."]);
        }

        // Company and user share one transaction: a rejected password or email must not leave a company (and its slug) behind.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var company = await companies.CreateAsync(request.CompanyName, cancellationToken);
        var user = new PortalUser { UserName = request.Email, Email = request.Email, CompanyId = company.Id };
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Problems([.. created.Errors.Select(e => e.Description)]);
        }

        await transaction.CommitAsync(cancellationToken);
        await signIn.SignInAsync(user, isPersistent: true);
        return Results.Json(new MeResponse(request.Email, company.Id, company.Name), statusCode: 201);
    }

    private static async Task<IResult> LoginAsync(LoginRequest? request, UserManager<PortalUser> users, SignInManager<PortalUser> signIn, ICompanyService companies, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrEmpty(request.Password))
        {
            return Problems(["Email and password are required."]);
        }

        var user = await users.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return InvalidLogin();
        }

        var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: true, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            return InvalidLogin();
        }

        var company = await companies.FindAsync(user.CompanyId, cancellationToken);
        return Results.Json(new MeResponse(request.Email, user.CompanyId, company?.Name ?? string.Empty));
    }

    private static async Task<IResult> LogoutAsync(SignInManager<PortalUser> signIn)
    {
        await signIn.SignOutAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, UserManager<PortalUser> users, ICompanyService companies, CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var company = await companies.FindAsync(user.CompanyId, cancellationToken);
        return Results.Json(new MeResponse(user.Email ?? string.Empty, user.CompanyId, company?.Name ?? string.Empty));
    }

    private static async Task<IResult> ListKeysAsync(ClaimsPrincipal principal, UserManager<PortalUser> users, IApiKeyService keys, CancellationToken cancellationToken)
    {
        if (await CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var list = await keys.ListAsync(companyId, cancellationToken);
        return Results.Json(list.Select(k => new ApiKeyResponse(k.Id, k.Name, k.Prefix, k.CreatedAt, k.ExpiresAt, k.LastUsedAt, k.Disabled)));
    }

    private static async Task<IResult> CreateKeyAsync(CreateKeyRequest? request, ClaimsPrincipal principal, UserManager<PortalUser> users, IApiKeyService keys, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return Problems(["A key name is required."]);
        }

        if (request.ExpiresAt is { } expiresAt && expiresAt <= time.GetUtcNow())
        {
            return Problems(["The expiry must be in the future."]);
        }

        var created = await keys.CreateAsync(companyId, request.Name, request.ExpiresAt, cancellationToken);
        return Results.Json(new CreatedApiKeyResponse(created.Id, created.Name, created.Prefix, created.CreatedAt, created.ExpiresAt, created.RawKey), statusCode: 201);
    }

    private static async Task<IResult> RevokeKeyAsync(Guid id, ClaimsPrincipal principal, UserManager<PortalUser> users, IApiKeyService keys, CancellationToken cancellationToken)
    {
        if (await CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        return await keys.RevokeAsync(companyId, id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ConnectionsAsync(ClaimsPrincipal principal, UserManager<PortalUser> users, ConnectionRegistry registry, MCPalDbContext db, CancellationToken cancellationToken)
    {
        if (await CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var keyNames = await db.ApiKeys.AsNoTracking().Where(k => k.CompanyId == companyId).ToDictionaryAsync(k => k.Id, k => k.Name, cancellationToken);
        var connections = registry.Connections(companyId).Select(c => new ConnectionResponse(
            c.AgentName,
            c.ConnectedAt,
            keyNames.GetValueOrDefault(c.ApiKeyId),
            [.. c.Servers.Select(s => new ServerResponse(s.Name, [.. s.Tools.Select(t => t.Descriptor.Name)]))],
            [.. c.RejectedServers.Select(r => new RejectedServerResponse(r.ServerName, r.Reason))],
            [.. c.RejectedTools.Select(r => new RejectedToolResponse(r.ServerName, r.ToolName, r.Reason))]));
        return Results.Json(connections);
    }

    private static IResult ConnectInfo(IOptions<McpalOptions> options)
    {
        var baseUrl = options.Value.PublicUrl.TrimEnd('/');
        var url = baseUrl + "/mcp";
        return Results.Json(new ConnectInfoResponse(
            url,
            baseUrl,
            $"claude mcp add --transport http mcpal {url}",
            $"claude mcp add --transport http mcpal {url} --header \"Authorization: Bearer <api-key>\""));
    }

    private static async Task<Guid?> CompanyOfAsync(ClaimsPrincipal principal, UserManager<PortalUser> users) =>
        (await users.GetUserAsync(principal))?.CompanyId;

    internal static IResult Problems(IEnumerable<string> errors, int status = 400) => Results.Json(new { errors = errors.ToArray() }, statusCode: status);

    private static IResult InvalidLogin() => Problems(["Invalid email or password."], 401);
}

/// <summary>Validates the anti-forgery header on mutating portal calls.</summary>
internal sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return await next(context);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return PortalEndpoints.Problems(["Invalid or missing anti-forgery token."]);
        }

        return await next(context);
    }
}
