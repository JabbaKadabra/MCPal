using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Server.Access;
using MCPal.Server.Access.UserContext;
using MCPal.Server.Audit;
using MCPal.Server.Diagnostics;
using MCPal.Server.Mcp;
using MCPal.Server.OAuth;
using MCPal.Server.Portal;
using MCPal.Server.Tenancy;
using MCPal.Server.Tunnel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;

namespace MCPal.Server;

/// <summary>Registers the application services: tenancy, access control, OAuth, audit, tunnel registry and the tool call relay.</summary>
/// <param name="forWebHost">
/// True for the web host: it adds the background services and <see cref="CallRelay"/> (which needs the bridge invoker of the web ring).
/// False for harnesses that need the services only.
/// </param>
public sealed class ApplicationModule(bool forWebHost = true) : Module
{
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
        services.AddDataProtection().SetApplicationName("MCPal");
        services.AddOptions<KeyManagementOptions>().Configure<IOptions<McpalOptions>, ILoggerFactory>((keys, mcpal, loggers) =>
        {
            if (mcpal.Value.DataProtectionPath is { Length: > 0 } path)
            {
                keys.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(path), loggers);
            }
        });
        builder.Populate(services);

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
        if (forWebHost)
        {
            builder.RegisterType<SigningKeyRotationService>().As<IHostedService>().SingleInstance();
            builder.RegisterType<OAuthCleanupService>().As<IHostedService>().SingleInstance();
            builder.RegisterType<TunnelSweepService>().As<IHostedService>().SingleInstance();
            builder.Register(context => context.Resolve<AuditWriter>()).As<IHostedService>().SingleInstance();
            builder.RegisterType<AuditCleanupService>().As<IHostedService>().SingleInstance();
            builder.RegisterType<CallRelay>().AsSelf().InstancePerLifetimeScope();
        }

        builder.RegisterType<OAuthService>().As<IOAuthService>().As<IAccessTokenValidator>().InstancePerLifetimeScope();
        builder.RegisterType<OAuthTokenRevoker>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<ApiKeyService>().As<IApiKeyService>().InstancePerLifetimeScope();
        builder.RegisterType<CompanyService>().As<ICompanyService>().InstancePerLifetimeScope();
    }
}
