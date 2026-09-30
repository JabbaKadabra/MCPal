using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Server;
using MCPal.Server.OAuth;
using MCPal.Server.Portal;
using MCPal.Server.Tenancy;
using MCPal.Server.Tunnel;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(container => container.RegisterModule(new ServerModule()));

var app = builder.Build();

app.UseForwardedHeaders();
app.MapStaticAssets();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Anonymous endpoints: the default writer prints the status only, never exception details.
var readiness = new HealthCheckOptions { Predicate = check => check.Tags.Contains(ServerWebServices.ReadyHealthTag) };
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", readiness);
app.MapHealthChecks("/health", readiness);
OAuthEndpoints.Map(app);
PortalEndpoints.Map(app);
app.MapHub<BridgeHub>("/hub/bridge");
app.MapMcp("/mcp")
    .RequireAuthorization(McpBearerDefaults.McpPolicy)
    .RequireRateLimiting(ServerWebServices.McpRateLimitPolicy);

// Client-side routes of the React SPA. API, MCP, tunnel and OAuth machine endpoints never fall back to index.html.
app.MapFallbackToFile(@"{*path:regex(^(?!api(/|$)|mcp(/|$)|health(/|$)|hub(/|$)|\.well-known(/|$)|oauth/(token|register)(/|$)).*$)}", "index.html");

app.MapFallbackToFile("/", "index.html");

app.Run();
