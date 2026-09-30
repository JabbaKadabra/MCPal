using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Cloud;
using MCPal.Cloud.OAuth;
using MCPal.Cloud.Portal;
using MCPal.Cloud.Tenancy;
using MCPal.Cloud.Tunnel;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(container => container.RegisterModule(new CloudModule()));

var app = builder.Build();

app.UseForwardedHeaders();
app.MapStaticAssets();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Anonymous endpoints: the default writer prints the status only, never exception details.
var readiness = new HealthCheckOptions { Predicate = check => check.Tags.Contains(CloudWebServices.ReadyHealthTag) };
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", readiness);
app.MapHealthChecks("/health", readiness);
OAuthEndpoints.Map(app);
PortalEndpoints.Map(app);
app.MapHub<AgentHub>("/hub/agent");
app.MapMcp("/mcp")
    .RequireAuthorization(McpBearerDefaults.McpPolicy)
    .RequireRateLimiting(CloudWebServices.McpRateLimitPolicy);

// Client-side routes of the React SPA. API, MCP, tunnel and OAuth machine endpoints never fall back to index.html.
app.MapFallbackToFile(@"{*path:regex(^(?!api(/|$)|mcp(/|$)|health(/|$)|hub(/|$)|\.well-known(/|$)|oauth/(token|register)(/|$)).*$)}", "index.html");

app.MapFallbackToFile("/", "index.html");

app.Run();
