using Autofac;

namespace MCPal.Cloud;

/// <summary>
/// Registers all MCPal.Cloud services. Hosts and test harnesses register this module instead of individual services.
/// </summary>
public sealed class CloudModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RegisterInstance(TimeProvider.System).As<TimeProvider>().IfNotRegistered(typeof(TimeProvider));
    }
}
