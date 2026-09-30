using Autofac;
using MCPal.Agent.Config;
using MCPal.Agent.Local;
using MCPal.Agent.Tunnel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MCPal.Agent;

/// <summary>
/// Registers all MCPal.Agent services. Hosts and test harnesses register this module instead of individual services.
/// </summary>
/// <param name="runTunnel">False for the <c>check</c> verb: local servers only, no cloud connection.</param>
public sealed class AgentModule(bool runTunnel = true) : Module
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
                return AgentConfigLoader.Load(ResolveConfigPath(configuration), AgentConfigLoader.CurrentEnvironment(), requireCloud: runTunnel);
            })
            .AsSelf()
            .SingleInstance();
        builder.RegisterType<LocalServerManager>().As<ILocalServerManager>().AsSelf().SingleInstance();
        builder.RegisterType<DefaultTunnelTransportConfigurator>().As<ITunnelTransportConfigurator>().SingleInstance();
        if (runTunnel)
        {
            builder.RegisterType<TunnelClient>().As<IHostedService>().SingleInstance();
        }
    }
}
