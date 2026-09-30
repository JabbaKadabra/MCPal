using System.Threading.RateLimiting;
using MCPal.Cloud.Mcp;
using MCPal.Cloud.Portal;
using MCPal.Cloud.Storage;
using MCPal.Cloud.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MCPal.Cloud;

/// <summary>ASP.NET Core pipeline services (authentication, SignalR, MCP, ...).</summary>
internal static class CloudWebServices
{
    public const string McpRateLimitPolicy = "mcp";

    public static void Register(IServiceCollection services)
    {
        services.AddHealthChecks();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, McpBearerAuthenticationHandler>(McpBearerDefaults.Scheme, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(McpBearerDefaults.McpPolicy, policy => policy
                .AddAuthenticationSchemes(McpBearerDefaults.Scheme)
                .RequireAuthenticatedUser())
            .AddPolicy(McpBearerDefaults.TunnelPolicy, policy => policy
                .AddAuthenticationSchemes(McpBearerDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(McpalClaims.AuthKind, McpalClaims.AuthKindApiKey));

        services.AddIdentity<PortalUser, IdentityRole>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireDigit = false;
                options.Lockout.MaxFailedAccessAttempts = 8;
            })
            .AddEntityFrameworkStores<MCPalDbContext>();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "mcpal.portal";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(PortalEndpoints.Policy, policy => policy
                .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
                .RequireAuthenticatedUser());
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "mcpal.csrf";
            options.Cookie.SameSite = SameSiteMode.Lax;
        });
        services.AddDataProtection().SetApplicationName("MCPal");
        services.AddOptions<KeyManagementOptions>().Configure<IOptions<McpalOptions>, ILoggerFactory>((keys, mcpal, loggers) =>
        {
            if (mcpal.Value.DataProtectionPath is { Length: > 0 } path)
            {
                keys.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(path), loggers);
            }
        });

        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        });

        services.AddMcpServer().WithHttpTransport(options =>
        {
            options.Stateless = true;
            options.ConfigureSessionOptions = (context, serverOptions, _) =>
            {
                var companyId = context.User.GetCompanyId()
                    ?? throw new InvalidOperationException("MCP request without an authenticated company.");
                context.RequestServices.GetRequiredService<TenantToolHandlers>().Configure(serverOptions, companyId);
                return Task.CompletedTask;
            };
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PortalEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                "ip:" + context.Connection.RemoteIpAddress,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy(McpRateLimitPolicy, context =>
            {
                var limit = context.RequestServices.GetRequiredService<IOptions<McpalOptions>>().Value.McpRequestsPerMinute;
                var authorization = context.Request.Headers.Authorization.ToString();
                var partition = authorization.Length > 0
                    ? ApiKeyService.Hash(authorization)
                    : "ip:" + context.Connection.RemoteIpAddress;
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
    }
}
