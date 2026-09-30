using MCPal.Cloud.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace MCPal.Cloud;

/// <summary>ASP.NET Core pipeline services (authentication, SignalR, MCP, ...).</summary>
internal static class CloudWebServices
{
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
    }
}
