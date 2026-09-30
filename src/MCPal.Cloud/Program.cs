using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Cloud;
using MCPal.Cloud.Tenancy;
using MCPal.Cloud.Tunnel;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(container => container.RegisterModule(new CloudModule()));

var app = builder.Build();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapHub<AgentHub>("/hub/agent");
app.MapMcp("/mcp")
    .RequireAuthorization(McpBearerDefaults.McpPolicy)
    .RequireRateLimiting(CloudWebServices.McpRateLimitPolicy);

app.Run();
