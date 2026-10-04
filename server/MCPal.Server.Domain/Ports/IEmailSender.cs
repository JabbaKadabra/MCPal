namespace MCPal.Server.Portal;

/// <summary>Sends the mails of the portal: account confirmation, password reset and invitations.</summary>
internal interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken cancellationToken);
}
