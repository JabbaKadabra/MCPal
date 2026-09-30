using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Cloud;
using MCPal.Cloud.OAuth;
using MCPal.Cloud.Portal;
using MCPal.Cloud.Tenancy;
using MCPal.Cloud.Tunnel;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(container => container.RegisterModule(new CloudModule()));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health");
OAuthEndpoints.Map(app);
PortalEndpoints.Map(app);
app.MapHub<AgentHub>("/hub/agent");
app.MapMcp("/mcp")
    .RequireAuthorization(McpBearerDefaults.McpPolicy)
    .RequireRateLimiting(CloudWebServices.McpRateLimitPolicy);

// Client-side routes of the React SPA. API, MCP, tunnel and OAuth machine endpoints never fall back to index.html.
app.MapFallbackToFile(@"{*path:regex(^(?!api(/|$)|mcp(/|$)|hub(/|$)|\.well-known(/|$)|oauth/(token|register)(/|$)).*$)}", "index.html");

app.Run();
