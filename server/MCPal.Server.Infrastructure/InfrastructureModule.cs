using Autofac;
using MCPal.Server.Portal;
using Microsoft.Extensions.Options;

namespace MCPal.Server;

/// <summary>Registers the adapters for the outside world: the mail sender (SMTP, or the log when no host is configured).</summary>
public sealed class InfrastructureModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RegisterType<LogEmailSender>().AsSelf().SingleInstance();
        builder.RegisterType<SmtpEmailSender>().AsSelf().SingleInstance();
        builder.Register<IEmailSender>(context =>
                string.IsNullOrWhiteSpace(context.Resolve<IOptions<McpalOptions>>().Value.Smtp.Host)
                    ? context.Resolve<LogEmailSender>()
                    : context.Resolve<SmtpEmailSender>())
            .SingleInstance();
    }
}
