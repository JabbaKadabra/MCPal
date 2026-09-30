using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Cloud;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(container => container.RegisterModule(new CloudModule()));

var app = builder.Build();

app.MapHealthChecks("/health");

app.Run();
