using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Server.Mcp;
using MCPal.Server.Tunnel;
using Microsoft.Extensions.DependencyInjection;

namespace MCPal.Server;

/// <summary>Registers the ASP.NET Core pipeline services (authentication, SignalR, MCP, rate limiting, telemetry) and the web adapters of the application ports.</summary>
public sealed class WebModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = new ServiceCollection();
        ServerWebServices.Register(services);
        builder.Populate(services);

        builder.RegisterType<TenantToolHandlers>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<HubBridgeInvoker>().As<IBridgeInvoker>().SingleInstance();
    }
}
