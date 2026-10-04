using System.Threading.RateLimiting;
using MCPal.Server.Diagnostics;
using MCPal.Server.Mcp;
using MCPal.Server.Portal;
using MCPal.Server.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MCPal.Server;

/// <summary>ASP.NET Core pipeline services (authentication, SignalR, MCP, ...).</summary>
internal static class ServerWebServices
{
    public const string McpRateLimitPolicy = "mcp";

    /// <summary>Npgsql 9+ emits its own spans (<c>ActivitySource</c>) and metrics (<c>Meter</c>) under this name; no extra package is needed.</summary>
    private const string NpgsqlTelemetryName = "Npgsql";

    /// <summary>
    /// Traces and metrics. The OTLP exporter is on only when the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> variable is set
    /// (its other <c>OTEL_*</c> variables are read by the exporter itself), so a deployment without a collector exports nothing.
    /// </summary>
    private static void RegisterTelemetry(IServiceCollection services)
    {
        var export = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
        var telemetry = services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("mcpal-server"));

        // This collection is populated into the container after the host's services. OpenTelemetry adds a fallback
        // IConfiguration (environment variables only) when none is registered, which would replace the host's.
        services.RemoveAll<Microsoft.Extensions.Configuration.IConfiguration>();
        telemetry.WithTracing(tracing =>
        {
            tracing.AddSource(ServerTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource(NpgsqlTelemetryName);
            if (export)
            {
                tracing.AddOtlpExporter();
            }
        });
        telemetry.WithMetrics(metrics =>
        {
            metrics.AddMeter(ServerTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(NpgsqlTelemetryName);
            if (export)
            {
                metrics.AddOtlpExporter();
            }
        });
    }

    public static void Register(IServiceCollection services)
    {
        RegisterTelemetry(services);

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, McpBearerAuthenticationHandler>(McpBearerDefaults.Scheme, null)
            .AddPolicyScheme(McpBearerDefaults.SelectorScheme, null, options => options.ForwardDefaultSelector = context =>
                McpBearerAuthenticationHandler.HasBearer(context.Request) ? McpBearerDefaults.Scheme : IdentityConstants.ApplicationScheme);

        // UseAuthentication must know the bearer principal before the rate limiter partitions on it. PostConfigure because
        // AddIdentity sets the cookie as default authenticate scheme.
        services.PostConfigure<AuthenticationOptions>(options => options.DefaultAuthenticateScheme = McpBearerDefaults.SelectorScheme);
        services.AddAuthorizationBuilder()
            // /mcp is for users: personal access tokens and OAuth tokens carry a user, bridge keys do not and are refused.
            .AddPolicy(McpBearerDefaults.McpPolicy, policy => policy
                .AddAuthenticationSchemes(McpBearerDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(McpalClaims.UserId))
            .AddPolicy(McpBearerDefaults.TunnelPolicy, policy => policy
                .AddAuthenticationSchemes(McpBearerDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(McpalClaims.AuthKind, McpalClaims.AuthKindApiKey)
                .RequireAssertion(context => context.User.GetKeyPurpose() is ApiKeyPurpose.Bridge));

        services.AddIdentity<PortalUser, IdentityRole>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // New accounts must confirm their address before a password login works. A migration confirms the
                // accounts that existed before, so nobody is locked out.
                options.SignIn.RequireConfirmedEmail = true;
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireDigit = false;
                options.Lockout.MaxFailedAccessAttempts = 8;
            })
            .AddDefaultTokenProviders();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "mcpal.portal";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        // Behind a TLS-terminating proxy the client IP (rate limits) and the scheme (Secure cookies) come from
        // X-Forwarded-For/-Proto, accepted only from loopback and the configured proxy networks.
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<McpalOptions>>((forwarded, mcpal) =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var network in mcpal.Value.TrustedProxyNetworks)
            {
                forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
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
                var caller = context.User.GetCallerIdentity()
                    ?? throw new InvalidOperationException("MCP request without an authenticated company.");
                context.RequestServices.GetRequiredService<TenantToolHandlers>().Configure(serverOptions, caller, context.RequestAborted);
                return Task.CompletedTask;
            };
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "unknown";
                context.HttpContext.RequestServices.GetRequiredService<ServerTelemetry>().RecordRateLimitRejection(policy);
                return ValueTask.CompletedTask;
            };
            options.AddPolicy(PortalEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                "ip:" + context.Connection.RemoteIpAddress,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy(McpRateLimitPolicy, context =>
            {
                var limit = context.RequestServices.GetRequiredService<IOptions<McpalOptions>>().Value.McpRequestsPerMinute;

                // Validated principals only: a request with an unknown token shares its IP's bucket instead of getting its own.
                var principal = context.User;
                var partition = principal.Identity is { IsAuthenticated: true, AuthenticationType: McpBearerDefaults.Scheme } && principal.GetMcpalUserId() is { } userId
                    ? "user:" + userId
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
