using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Server.Access;
using MCPal.Server.Access.UserContext;
using MCPal.Server.Audit;
using MCPal.Server.Diagnostics;
using MCPal.Server.Mcp;
using MCPal.Server.OAuth;
using MCPal.Server.Portal;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using MCPal.Server.Tunnel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
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
        services.AddSingleton<IValidateOptions<McpalOptions>, McpalEnvironmentValidator>();
        services.AddOptions<McpalOptions>()
            .BindConfiguration(McpalOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddDbContext<MCPalDbContext>((provider, options) =>
        {
            var configuration = provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            options.UseNpgsql(configuration.GetConnectionString(ConnectionStringName));
        });
        services.AddDataProtection().SetApplicationName("MCPal");
        services.AddOptions<KeyManagementOptions>().Configure<IOptions<McpalOptions>, ILoggerFactory>((keys, mcpal, loggers) =>
        {
            if (mcpal.Value.DataProtectionPath is { Length: > 0 } path)
            {
                keys.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(path), loggers);
            }
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
        builder.RegisterType<AccessPolicyCache>().AsSelf().SingleInstance();
        builder.RegisterType<AccessPolicyLoader>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<AccessEvaluator>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<AccessService>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<SigningKeyStore>().AsSelf().SingleInstance();
        builder.RegisterType<UserContextIssuer>().AsSelf().SingleInstance();
        builder.RegisterType<ServerTelemetry>().AsSelf().SingleInstance();
        builder.RegisterType<AuditWriter>().AsSelf().As<IAuditSink>().SingleInstance();
        builder.RegisterType<AuditRetention>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<TunnelTerminator>().As<IApiKeyRevocationListener>().SingleInstance();
        builder.RegisterType<TunnelSweeper>().AsSelf().InstancePerLifetimeScope();
        if (registerWebServices)
        {
            builder.RegisterType<DatabaseMigrator>().As<IHostedService>().SingleInstance();
            builder.RegisterType<SigningKeyRotationService>().As<IHostedService>().SingleInstance();
            builder.RegisterType<OAuthCleanupService>().As<IHostedService>().SingleInstance();
            builder.RegisterType<TunnelSweepService>().As<IHostedService>().SingleInstance();
            builder.Register(context => context.Resolve<AuditWriter>()).As<IHostedService>().SingleInstance();
            builder.RegisterType<AuditCleanupService>().As<IHostedService>().SingleInstance();
            builder.RegisterType<CallRelay>().AsSelf().InstancePerLifetimeScope();
            builder.RegisterType<TenantToolHandlers>().AsSelf().InstancePerLifetimeScope();
            builder.RegisterType<HubBridgeInvoker>().As<IBridgeInvoker>().SingleInstance();
        }

        builder.RegisterType<OAuthService>().As<IOAuthService>().As<IAccessTokenValidator>().InstancePerLifetimeScope();
        builder.RegisterType<OAuthTokenRevoker>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<ApiKeyService>().As<IApiKeyService>().InstancePerLifetimeScope();
        builder.RegisterType<CompanyService>().As<ICompanyService>().InstancePerLifetimeScope();
    }
}
