using Autofac;
using MCPal.Server.Storage;

namespace MCPal.Server;

/// <summary>
/// Registers all MCPal.Server services. Hosts and test harnesses register this module instead of individual services.
/// It composes the ring modules: application, storage, infrastructure and the ASP.NET Core pipeline.
/// </summary>
/// <param name="registerWebServices">
/// True for the web host: the ASP.NET Core pipeline services (<see cref="WebModule"/>) plus what only a running host needs: startup migration,
/// ASP.NET Identity stores, database health check, background services and <c>CallRelay</c>.
/// False for harnesses that need only storage, application and infrastructure services.
/// </param>
public sealed class ServerModule(bool registerWebServices = true) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Hosted services start in registration order: the database migration (storage) must run before the background services (application).
        builder.RegisterModule(new StorageModule(registerWebServices));
        builder.RegisterModule(new ApplicationModule(registerWebServices));
        builder.RegisterModule(new InfrastructureModule());
        if (registerWebServices)
        {
            builder.RegisterModule(new WebModule());
        }
    }
}
