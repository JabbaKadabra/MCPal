using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Cloud;
using MCPal.Cloud.Tunnel;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(container => container.RegisterModule(new CloudModule()));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapHub<AgentHub>("/hub/agent");

app.Run();
