using MCPal.Server.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace MCPal.Server.Portal;

internal sealed record ConfirmEmailRequest(string? UserId, string? Token);

internal sealed record EmailRequest(string? Email);

internal sealed record ResetPasswordRequest(string? Email, string? Token, string? NewPassword);

/// <summary>
/// Email confirmation and password reset. Under the portal group, so they carry its rate limit and the anti-forgery check.
/// The mail-triggering endpoints answer 204 whatever the address, so they cannot be used to find out who has an account.
/// </summary>
internal static class AccountEndpoints
{
    private const string InvalidLink = "This link is invalid or has expired.";

    public static void Map(RouteGroupBuilder auth)
    {
        ArgumentNullException.ThrowIfNull(auth);

        auth.MapPost("confirm-email", ConfirmEmailAsync);
        auth.MapPost("resend-confirmation", ResendConfirmationAsync);
        auth.MapPost("forgot-password", ForgotPasswordAsync);
        auth.MapPost("reset-password", ResetPasswordAsync);
    }

    private static async Task<IResult> ConfirmEmailAsync(ConfirmEmailRequest? request, UserManager<PortalUser> users)
    {
        if (request?.UserId is null || !AccountTokens.TryDecode(request.Token, out var token) || await users.FindByIdAsync(request.UserId) is not { } user)
        {
            return PortalEndpoints.Problems([InvalidLink]);
        }

        var result = await users.ConfirmEmailAsync(user, token);
        return result.Succeeded ? Results.NoContent() : PortalEndpoints.Problems([InvalidLink]);
    }

    private static async Task<IResult> ResendConfirmationAsync(EmailRequest? request, UserManager<PortalUser> users, AccountMailer mailer, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request?.Email) && await users.FindByEmailAsync(request.Email) is { EmailConfirmed: false } user)
        {
            await mailer.SendConfirmationAsync(user, cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ForgotPasswordAsync(EmailRequest? request, UserManager<PortalUser> users, AccountMailer mailer, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request?.Email) && await users.FindByEmailAsync(request.Email) is { } user)
        {
            await mailer.SendPasswordResetAsync(user, cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ResetPasswordAsync(ResetPasswordRequest? request, UserManager<PortalUser> users)
    {
        if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrEmpty(request.NewPassword)
            || !AccountTokens.TryDecode(request.Token, out var token) || await users.FindByEmailAsync(request.Email) is not { } user)
        {
            return PortalEndpoints.Problems([InvalidLink]);
        }

        var result = await users.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
        {
            return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
                ? PortalEndpoints.Problems([InvalidLink])
                : PortalEndpoints.Problems([.. result.Errors.Select(e => e.Description)]);
        }

        // The link came to the mailbox, so the address is proven.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
        }

        return Results.NoContent();
    }
}
