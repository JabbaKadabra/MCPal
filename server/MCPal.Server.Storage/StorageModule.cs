using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Server.Audit;
using MCPal.Server.Ports;
using MCPal.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MCPal.Server.Storage;

/// <summary>Registers the PostgreSQL database: <see cref="MCPalDbContext"/>, the <see cref="IMcpalData"/> port and the startup migration.</summary>
/// <param name="forWebHost">
/// True for the web host: it adds the startup migration, the ASP.NET Identity stores and the database health check.
/// False for harnesses that need only the database.
/// </param>
public sealed class StorageModule(bool forWebHost = true) : Module
{
    public const string ConnectionStringName = "Mcpal";

    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = new ServiceCollection();
        services.AddDbContext<MCPalDbContext>((provider, options) =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            options.UseNpgsql(configuration.GetConnectionString(ConnectionStringName));
        });
        services.AddScoped<IMcpalData>(provider => provider.GetRequiredService<MCPalDbContext>());
        if (forWebHost)
        {
            // The web host signs portal users in through ASP.NET Identity (AddIdentity itself is in the web ring) and reports readiness from the database.
            new IdentityBuilder(typeof(PortalUser), typeof(IdentityRole), services).AddEntityFrameworkStores<MCPalDbContext>();
            services.AddHealthChecks().AddDbContextCheck<MCPalDbContext>(tags: [HealthTags.Ready]);
        }

        builder.Populate(services);

        builder.RegisterType<PostgresAuditSearch>().As<IAuditSearch>().SingleInstance();

        if (forWebHost)
        {
            builder.RegisterType<DatabaseMigrator>().As<IHostedService>().SingleInstance();
        }
    }
}
