using Autofac;

namespace MCPal.Agent;

/// <summary>
/// Registers all MCPal.Agent services. Hosts and test harnesses register this module instead of individual services.
/// </summary>
public sealed class AgentModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RegisterInstance(TimeProvider.System).As<TimeProvider>().IfNotRegistered(typeof(TimeProvider));
    }
}
