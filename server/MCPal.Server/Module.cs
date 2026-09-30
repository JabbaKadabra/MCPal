using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Server.Audit;
using MCPal.Server.Diagnostics;
using MCPal.Server.Mcp;
using MCPal.Server.OAuth;
using MCPal.Server.Portal;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tunnel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace MCPal.Server;

/// <summary>
/// Registers all MCPal.Server services. Hosts and test harnesses register this module instead of individual services.
/// </summary>
/// <param name="registerWebServices">
/// False for harnesses that need only storage and domain services and no ASP.NET Core pipeline services.
/// </param>
public sealed class ServerModule(bool registerWebServices = true) : Module
{
    public const string ConnectionStringName = "Mcpal";

    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RegisterInstance(TimeProvider.System).As<TimeProvider>().IfNotRegistered(typeof(TimeProvider));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
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
            ServerWebServices.Register(services);
        }

        builder.Populate(services);

        builder.RegisterType<LogEmailSender>().AsSelf().SingleInstance();
        builder.RegisterType<SmtpEmailSender>().AsSelf().SingleInstance();
        builder.Register<IEmailSender>(context =>
                string.IsNullOrWhiteSpace(context.Resolve<IOptions<McpalOptions>>().Value.Smtp.Host)
                    ? context.Resolve<LogEmailSender>()
                    : context.Resolve<SmtpEmailSender>())
            .SingleInstance();
        builder.RegisterType<AccountMailer>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<TeamService>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<ConnectionRegistry>().AsSelf().SingleInstance();
        builder.RegisterType<ServerTelemetry>().AsSelf().SingleInstance();
        builder.RegisterType<AuditWriter>().AsSelf().As<IAuditSink>().SingleInstance();
        builder.RegisterType<AuditRetention>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<TunnelTerminator>().As<IApiKeyRevocationListener>().SingleInstance();
        builder.RegisterType<TunnelSweeper>().AsSelf().InstancePerLifetimeScope();
        if (registerWebServices)
        {
            builder.RegisterType<DatabaseMigrator>().As<IHostedService>().SingleInstance();
            builder.RegisterType<OAuthCleanupService>().As<IHostedService>().SingleInstance();
            builder.RegisterType<TunnelSweepService>().As<IHostedService>().SingleInstance();
            builder.Register(context => context.Resolve<AuditWriter>()).As<IHostedService>().SingleInstance();
            builder.RegisterType<AuditCleanupService>().As<IHostedService>().SingleInstance();
            builder.RegisterType<CallRelay>().AsSelf().InstancePerLifetimeScope();
            builder.RegisterType<TenantToolHandlers>().AsSelf().InstancePerLifetimeScope();
            builder.RegisterType<HubBridgeInvoker>().As<IBridgeInvoker>().SingleInstance();
        }

        builder.RegisterType<OAuthService>().As<IOAuthService>().As<IAccessTokenValidator>().InstancePerLifetimeScope();
        builder.RegisterType<OAuthTokenRevoker>().As<IApiKeyRevocationListener>().InstancePerLifetimeScope();
        builder.RegisterType<ApiKeyService>().As<IApiKeyService>().InstancePerLifetimeScope();
        builder.RegisterType<CompanyService>().As<ICompanyService>().InstancePerLifetimeScope();
    }
}
