using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Agent;

var builder = Host.CreateApplicationBuilder(args);

builder.ConfigureContainer(new AutofacServiceProviderFactory(), container => container.RegisterModule<AgentModule>());

using var host = builder.Build();
await host.RunAsync();
