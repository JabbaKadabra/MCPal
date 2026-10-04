using System.Net;
using System.Text;
using MCPal.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace MCPal.Server.Portal;

/// <summary>Identity tokens contain characters that break links (<c>+ / =</c>); links carry them base64url encoded.</summary>
internal static class AccountTokens
{
    public static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    public static bool TryDecode(string? encoded, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encoded));
            return token.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Builds and sends the account mails. Links point to the SPA under <see cref="McpalOptions.PublicUrl"/>. A mail that cannot
/// be sent is logged and never fails the request: an error would tell an anonymous caller whether the address is known.
/// </summary>
internal sealed class AccountMailer(UserManager<PortalUser> users, IEmailSender email, IOptions<McpalOptions> options, ILogger<AccountMailer> logger)
{
    public async Task SendConfirmationAsync(PortalUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var token = AccountTokens.Encode(await users.GenerateEmailConfirmationTokenAsync(user));
        var link = Link("confirm-email", ("userId", user.Id), ("token", token));
        await SendAsync(
            user.Email,
            "Confirm your MCPal email address",
            "Welcome to MCPal. Confirm your email address to keep signing in with your password:",
            link,
            "Confirm email address",
            cancellationToken);
    }

    public async Task SendPasswordResetAsync(PortalUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var token = AccountTokens.Encode(await users.GeneratePasswordResetTokenAsync(user));
        var link = Link("reset-password", ("email", user.Email ?? string.Empty), ("token", token));
        await SendAsync(
            user.Email,
            "Reset your MCPal password",
            "Someone asked to reset the password of this MCPal account. If that was you, choose a new password here. If not, ignore this mail.",
            link,
            "Reset password",
            cancellationToken);
    }

    public async Task SendInvitationAsync(string to, string companyName, string token, CancellationToken cancellationToken)
    {
        var link = Link("accept-invitation", ("token", token));
        await SendAsync(
            to,
            $"You are invited to {companyName} on MCPal",
            $"You were invited to join {companyName} on MCPal. Set a password to accept:",
            link,
            "Accept invitation",
            cancellationToken);
    }

    private string Link(string route, params (string Name, string Value)[] query)
    {
        var url = options.Value.PublicUrl.TrimEnd('/') + "/" + route;
        return QueryHelpers.AddQueryString(url, query.Select(pair => new KeyValuePair<string, string?>(pair.Name, pair.Value)));
    }

    private async Task SendAsync(string? to, string subject, string intro, string link, string action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            return;
        }

        var text = $"{intro}\n\n{link}\n";
        var html = $"<p>{WebUtility.HtmlEncode(intro)}</p><p><a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(action)}</a></p><p>{WebUtility.HtmlEncode(link)}</p>";
        try
        {
            await email.SendAsync(to, subject, html, text, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not send the mail '{Subject}'", subject);
        }
    }
}
