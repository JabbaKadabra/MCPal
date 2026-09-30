using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MCPal.Cloud.Portal;

/// <summary>Sends the mails of the portal: account confirmation, password reset and invitations.</summary>
internal interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken cancellationToken);
}

/// <summary>Development fallback when no SMTP host is configured: the mail, including its links, goes to the log.</summary>
internal sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken cancellationToken)
    {
        logger.LogWarning("No SMTP host is configured (Mcpal:Smtp:Host); the mail to {To} is only logged.\nSubject: {Subject}\n{Body}", to, subject, textBody);
        return Task.CompletedTask;
    }
}

internal sealed class SmtpEmailSender(IOptions<McpalOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken cancellationToken)
    {
        var smtp = options.Value.Smtp;
        var message = BuildMessage(smtp.From ?? string.Empty, to, subject, htmlBody, textBody);
        using var client = new SmtpClient();
        var security = smtp.UseTls
            ? smtp.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls
            : SecureSocketOptions.None;
        await client.ConnectAsync(smtp.Host ?? throw new InvalidOperationException("Mcpal:Smtp:Host is not set."), smtp.Port, security, cancellationToken);
        if (!string.IsNullOrEmpty(smtp.User))
        {
            await client.AuthenticateAsync(smtp.User, smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    internal static MimeMessage BuildMessage(string from, string to, string subject, string htmlBody, string textBody)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody();
        return message;
    }
}
