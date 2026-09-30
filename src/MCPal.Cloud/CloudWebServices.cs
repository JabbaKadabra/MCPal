using System.Threading.RateLimiting;
using MCPal.Cloud.Mcp;
using MCPal.Cloud.Tenancy;
using Microsoft.AspNetCore.Authentication;
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
