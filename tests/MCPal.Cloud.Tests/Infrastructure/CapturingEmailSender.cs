using System.Collections.Concurrent;
using MCPal.Cloud.Portal;
using Microsoft.AspNetCore.WebUtilities;

namespace MCPal.Cloud.Tests.Infrastructure;

internal sealed record SentEmail(string To, string Subject, string HtmlBody, string TextBody)
{
    /// <summary>The first link in the text body, the way a user would click it.</summary>
    public Uri Link()
    {
        var start = TextBody.IndexOf("http", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the mail should contain a link");
        var end = TextBody.IndexOfAny([' ', '\r', '\n'], start);
        return new Uri(end < 0 ? TextBody[start..] : TextBody[start..end]);
    }

    public string QueryValue(string name) => QueryHelpers.ParseQuery(Link().Query)[name].ToString();
}

/// <summary>Stands in for SMTP in tests and keeps what the portal sent.</summary>
internal sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<SentEmail> sent = new();

    public IReadOnlyList<SentEmail> Sent => [.. sent];

    public IReadOnlyList<SentEmail> To(string address) => [.. sent.Where(mail => string.Equals(mail.To, address, StringComparison.OrdinalIgnoreCase))];

    public Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken cancellationToken)
    {
        sent.Enqueue(new SentEmail(to, subject, htmlBody, textBody));
        return Task.CompletedTask;
    }
}
