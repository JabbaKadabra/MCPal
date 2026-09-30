using Autofac;
using MCPal.Server.Portal;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MCPal.Server.Tests.Portal;

[TestFixture]
internal sealed class EmailSenderTests : ServerTestBase
{
    [Test]
    public async Task IEmailSender_NoSmtpHost_IsTheLogSender()
    {
        var scope = await GetServicesAsync();

        scope.Resolve<IEmailSender>().Should().BeOfType<LogEmailSender>();
    }

    [Test]
    public async Task IEmailSender_SmtpHostConfigured_IsTheSmtpSender()
    {
        var scope = await GetServicesAsync(settings: new() { ["Mcpal:Smtp:Host"] = "smtp.example.com", ["Mcpal:Smtp:From"] = "mcpal@example.com" });

        scope.Resolve<IEmailSender>().Should().BeOfType<SmtpEmailSender>();
    }

    [Test]
    public async Task LogEmailSender_Send_WritesRecipientSubjectAndLinkToTheLog()
    {
        var provider = new CapturingLoggerProvider();
        var scope = await GetServicesAsync(builder => builder.RegisterInstance<ILoggerProvider>(provider));

        await scope.Resolve<IEmailSender>().SendAsync("user@acme.example", "Confirm", "<p>x</p>", "Click https://mcpal.example.com/confirm-email?token=abc", Ct);

        var entry = provider.Messages.Should().ContainSingle(m => m.Contains("user@acme.example", StringComparison.Ordinal)).Which;
        entry.Should().Contain("Confirm").And.Contain("https://mcpal.example.com/confirm-email?token=abc");
    }

    [Test]
    public void SmtpEmailSender_BuildMessage_HasSenderRecipientSubjectAndBothBodies()
    {
        var message = SmtpEmailSender.BuildMessage("MCPal <mcpal@example.com>", "user@acme.example", "Hello", "<p>html</p>", "plain");

        message.From.ToString().Should().Contain("mcpal@example.com");
        message.To.ToString().Should().Be("user@acme.example");
        message.Subject.Should().Be("Hello");
        message.HtmlBody.Should().Be("<p>html</p>");
        message.TextBody.Should().Be("plain");
    }

    [Test]
    public void Options_SmtpHostWithoutValidSender_FailsValidation()
    {
        var options = new McpalOptions { Smtp = { Host = "smtp.example.com" } };

        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options))
            .Should().Contain(r => r.ErrorMessage != null && r.ErrorMessage.Contains("Mcpal:Smtp:From", StringComparison.Ordinal));
    }

    [Test]
    public void Options_SmtpHostWithValidSender_PassesValidation()
    {
        var options = new McpalOptions { Smtp = { Host = "smtp.example.com", From = "mcpal@example.com", Port = 587 } };

        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Should().BeEmpty();
    }

    [Test]
    public void Options_NoSmtpHost_PassesValidation()
    {
        var options = new McpalOptions();

        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Should().BeEmpty();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new();

        public IReadOnlyCollection<string> Messages => messages;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
