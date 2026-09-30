using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Cloud.Storage;
using MCPal.Cloud.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MCPal.Cloud;

/// <summary>
/// Registers all MCPal.Cloud services. Hosts and test harnesses register this module instead of individual services.
/// </summary>
/// <param name="registerWebServices">
/// False for harnesses that need only storage and domain services and no ASP.NET Core pipeline services.
/// </param>
public sealed class CloudModule(bool registerWebServices = true) : Module
{
    public const string ConnectionStringName = "Mcpal";

    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RegisterInstance(TimeProvider.System).As<TimeProvider>().IfNotRegistered(typeof(TimeProvider));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<McpalOptions>()
            .BindConfiguration(McpalOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddDbContext<MCPalDbContext>((provider, options) =>
        {
            var configuration = provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            options.UseNpgsql(configuration.GetConnectionString(ConnectionStringName));
        });
        if (registerWebServices)
        {
            CloudWebServices.Register(services);
        }

        builder.Populate(services);

        builder.RegisterType<ApiKeyService>().As<IApiKeyService>().InstancePerLifetimeScope();
        builder.RegisterType<CompanyService>().As<ICompanyService>().InstancePerLifetimeScope();
    }
}
