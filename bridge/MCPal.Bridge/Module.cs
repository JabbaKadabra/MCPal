using Autofac;
using MCPal.Bridge.Config;
using MCPal.Bridge.Diagnostics;
using MCPal.Bridge.Local;
using MCPal.Bridge.Status;
using MCPal.Bridge.Tunnel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MCPal.Bridge;

/// <summary>
/// Registers all MCPal.Bridge services. Hosts and test harnesses register this module instead of individual services.
/// </summary>
/// <param name="runTunnel">False for the <c>check</c> verb: local servers only, no connection to the MCPal server.</param>
public sealed class BridgeModule(bool runTunnel = true) : Module
{
    public const string ConfigKey = "config";
    public const string DefaultConfigFileName = "mcpal.json";

    public static string ResolveConfigPath(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration[ConfigKey] ?? Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
    }

    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RegisterInstance(TimeProvider.System).As<TimeProvider>().IfNotRegistered(typeof(TimeProvider));

        builder.Register(context =>
            {
                var configuration = context.Resolve<IConfiguration>();
                return BridgeConfigLoader.Load(ResolveConfigPath(configuration), BridgeConfigLoader.CurrentEnvironment(), requireMcpal: runTunnel);
            })
            .AsSelf()
            .SingleInstance();
        builder.RegisterInstance(BridgeInfo.Current).AsSelf().IfNotRegistered(typeof(BridgeInfo));
        builder.RegisterType<BridgeTelemetry>().AsSelf().SingleInstance();
        builder.RegisterType<BridgeStatusTracker>().AsSelf().SingleInstance();
        builder.RegisterType<LocalServerManager>().As<ILocalServerManager>().AsSelf().SingleInstance();
        builder.RegisterType<DefaultTunnelTransportConfigurator>().As<ITunnelTransportConfigurator>().SingleInstance();
        if (runTunnel)
        {
            builder.RegisterType<JwksFileWriter>().AsSelf().As<IHostedService>().SingleInstance();
            builder.RegisterType<TunnelClient>().As<IHostedService>().SingleInstance();
        }
    }
}
