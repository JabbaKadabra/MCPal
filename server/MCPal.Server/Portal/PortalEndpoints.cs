using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MCPal.Server.Audit;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tunnel;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCPal.Server.Portal;

internal sealed record SignupRequest(string? CompanyName, string? Email, string? Password);

internal sealed record LoginRequest(string? Email, string? Password);

internal sealed record CreateKeyRequest(string? Name, DateTimeOffset? ExpiresAt, string? Purpose = null, IReadOnlyList<string>? AllowedServers = null);

/// <param name="Role"><c>owner</c> or <c>member</c>.</param>
internal sealed record MeResponse(string Email, Guid CompanyId, string CompanyName, string Role);

/// <param name="Purpose"><c>any</c>, <c>bridge</c> or <c>client</c>.</param>
internal sealed record ApiKeyResponse(
    Guid Id,
    string Name,
    string Prefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    bool Disabled,
    string Purpose,
    IReadOnlyList<string> AllowedServers,
    string? CreatedBy);

internal sealed record CreatedApiKeyResponse(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string Key, string Purpose, IReadOnlyList<string> AllowedServers);

internal sealed record ServerResponse(string Name, IReadOnlyList<string> Tools);

internal sealed record RejectedServerResponse(string Server, string Reason);

internal sealed record RejectedToolResponse(string Server, string Tool, string Reason);

internal sealed record ConnectionResponse(
    string BridgeName,
    string BridgeVersion,
    bool UpdateAvailable,
    string? LatestBridgeVersion,
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
        AccountEndpoints.Map(auth);
        auth.MapGet("me", MeAsync).RequireAuthorization(Policy);

        var secured = group.MapGroup(string.Empty).RequireAuthorization(Policy);
        TeamEndpoints.Map(group, secured);
        secured.MapGet("keys", ListKeysAsync);
        secured.MapPost("keys", CreateKeyAsync);
        secured.MapDelete("keys/{id:guid}", RevokeKeyAsync);
        secured.MapGet("connections", ConnectionsAsync);
        secured.MapGet("connect-info", ConnectInfo);
        AuditEndpoints.Map(secured);
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
        AccountMailer mailer,
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
        var user = new PortalUser { UserName = request.Email, Email = request.Email, CompanyId = company.Id, Role = PortalRole.Owner };
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Problems([.. created.Errors.Select(e => e.Description)]);
        }

        await transaction.CommitAsync(cancellationToken);

        // New accounts start unconfirmed and are signed in right away; the mail keeps later password logins possible.
        await mailer.SendConfirmationAsync(user, cancellationToken);
        await signIn.SignInAsync(user, isPersistent: true);
        return Results.Json(new MeResponse(request.Email, company.Id, company.Name, TeamEndpoints.RoleName(PortalRole.Owner)), statusCode: 201);
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
        if (result.IsNotAllowed)
        {
            // Sign-in checks the confirmation before the password. Only tell a caller who knows the password that the
            // address is unconfirmed; a wrong password must look like it does for every other account.
            if (!await users.CheckPasswordAsync(user, request.Password))
            {
                await users.AccessFailedAsync(user);
                return InvalidLogin();
            }

            return Results.Json(new { errors = EmailNotConfirmedErrors, error = "email_not_confirmed" }, statusCode: 403);
        }

        if (!result.Succeeded)
        {
            return InvalidLogin();
        }

        var company = await companies.FindAsync(user.CompanyId, cancellationToken);
        return Results.Json(new MeResponse(request.Email, user.CompanyId, company?.Name ?? string.Empty, TeamEndpoints.RoleName(user.Role)));
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
        return Results.Json(new MeResponse(user.Email ?? string.Empty, user.CompanyId, company?.Name ?? string.Empty, TeamEndpoints.RoleName(user.Role)));
    }

    private static async Task<IResult> ListKeysAsync(ClaimsPrincipal principal, UserManager<PortalUser> users, IApiKeyService keys, MCPalDbContext db, CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return Results.Unauthorized();
        }

        // Owners see every key of the company, members only the ones they created.
        var list = await keys.ListAsync(user.CompanyId, cancellationToken);
        var emails = await db.Users.AsNoTracking().Where(u => u.CompanyId == user.CompanyId).ToDictionaryAsync(u => u.Id, u => u.Email ?? string.Empty, cancellationToken);
        return Results.Json(list
            .Where(k => user.Role == PortalRole.Owner || k.CreatedByUserId == user.Id)
            .Select(k => new ApiKeyResponse(
                k.Id, k.Name, k.Prefix, k.CreatedAt, k.ExpiresAt, k.LastUsedAt, k.Disabled, PurposeName(k.Purpose), k.AllowedServers,
                k.CreatedByUserId is { } creator ? emails.GetValueOrDefault(creator) : null)));
    }

    private static async Task<IResult> CreateKeyAsync(CreateKeyRequest? request, ClaimsPrincipal principal, UserManager<PortalUser> users, IApiKeyService keys, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(principal) is not { } user)
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

        var purpose = ApiKeyPurpose.Any;
        if (request.Purpose is { Length: > 0 } purposeText && !Enum.TryParse(purposeText, ignoreCase: true, out purpose))
        {
            return Problems(["The purpose must be 'any', 'bridge' or 'client'."]);
        }

        // Members get access to Claude for themselves. Keys for bridges (and the legacy 'any') open tunnels and are the owners' business.
        if (user.Role != PortalRole.Owner && (request.Purpose is null || purpose != ApiKeyPurpose.Client))
        {
            return Problems(["Members can only create keys for Claude (purpose 'client'). Ask an owner for a bridge key."], 403);
        }

        var servers = request.AllowedServers ?? [];
        if (ServerRestrictionProblem(purpose, servers) is { } problem)
        {
            return Problems([problem]);
        }

        var created = await keys.CreateAsync(user.CompanyId, new NewApiKey(request.Name, request.ExpiresAt, purpose, servers, user.Id), cancellationToken);
        return Results.Json(
            new CreatedApiKeyResponse(created.Id, created.Name, created.Prefix, created.CreatedAt, created.ExpiresAt, created.RawKey, PurposeName(created.Purpose), created.AllowedServers),
            statusCode: 201);
    }

    private static async Task<IResult> RevokeKeyAsync(Guid id, ClaimsPrincipal principal, UserManager<PortalUser> users, IApiKeyService keys, CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return Results.Unauthorized();
        }

        // A key a member does not own looks like a key that does not exist.
        if (user.Role != PortalRole.Owner
            && (await keys.ListAsync(user.CompanyId, cancellationToken)).All(k => k.Id != id || k.CreatedByUserId != user.Id))
        {
            return Results.NotFound();
        }

        return await keys.RevokeAsync(user.CompanyId, id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ConnectionsAsync(ClaimsPrincipal principal, UserManager<PortalUser> users, ConnectionRegistry registry, MCPalDbContext db, IOptions<McpalOptions> options, CancellationToken cancellationToken)
    {
        if (await CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var keyNames = await db.ApiKeys.AsNoTracking().Where(k => k.CompanyId == companyId).ToDictionaryAsync(k => k.Id, k => k.Name, cancellationToken);
        var latest = string.IsNullOrWhiteSpace(options.Value.LatestBridgeVersion) ? null : options.Value.LatestBridgeVersion.Trim();
        var connections = registry.Connections(companyId).Select(c => new ConnectionResponse(
            c.BridgeName,
            c.BridgeVersion,
            BridgeVersions.IsOlder(c.BridgeVersion, latest),
            latest,
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

    private const int MaxAllowedServers = 50;

    private static readonly string[] EmailNotConfirmedErrors = ["Confirm your email address first. Use the link in the mail we sent you, or ask for a new one."];

    private static string PurposeName(ApiKeyPurpose purpose) => purpose.ToString().ToLowerInvariant();

    /// <summary>Servers may be named before they are online, so only their shape is checked.</summary>
    private static string? ServerRestrictionProblem(ApiKeyPurpose purpose, IReadOnlyList<string> servers)
    {
        if (servers.Count == 0)
        {
            return null;
        }

        if (purpose == ApiKeyPurpose.Bridge)
        {
            return "Bridge keys cannot be restricted to servers.";
        }

        if (servers.Count > MaxAllowedServers)
        {
            return $"At most {MaxAllowedServers} servers can be listed.";
        }

        return servers.Any(server => !ToolNaming.IsValidServerName(server)) ? "A server name is empty, longer than 200 characters or contains control characters." : null;
    }

    internal static async Task<Guid?> CompanyOfAsync(ClaimsPrincipal principal, UserManager<PortalUser> users) =>
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
