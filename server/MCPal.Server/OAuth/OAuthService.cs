using System.Security.Cryptography;
using System.Text;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCPal.Server.OAuth;

internal sealed record OAuthError(string Error, string Description);

internal sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn, string Scope);

/// <summary>A syntactically and semantically valid authorization request from a registered client.</summary>
internal sealed record AuthorizeRequest(
    OAuthClient Client,
    string RedirectUri,
    string CodeChallenge,
    string? State,
    string Scope,
    string? Resource);

/// <summary>Raw authorization request parameters as sent by the client.</summary>
internal sealed record AuthorizeParameters(
    string? ClientId,
    string? RedirectUri,
    string? ResponseType,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    string? State,
    string? Scope,
    string? Resource);

/// <summary>Result of validating an authorization request. Errors before the redirect URI is trusted must not redirect.</summary>
internal sealed record AuthorizeValidation(AuthorizeRequest? Request, OAuthError? Error, string? RedirectUri, string? State)
{
    /// <summary>True when the error may be reported to the client by redirecting (client and redirect URI are known-good).</summary>
    public bool CanRedirect => Error is not null && RedirectUri is not null;
}

internal interface IOAuthService
{
    Task<OAuthClient> RegisterClientAsync(string? clientName, IReadOnlyList<string> redirectUris, CancellationToken cancellationToken);

    Task<AuthorizeValidation> ValidateAuthorizeAsync(AuthorizeParameters parameters, CancellationToken cancellationToken);

    /// <summary>Creates a single-use code bound to the portal user who signed in and authorized the client.</summary>
    Task<string> IssueCodeAsync(AuthorizeRequest request, Guid companyId, string userId, CancellationToken cancellationToken);

    Task<(TokenResponse? Tokens, OAuthError? Error)> ExchangeCodeAsync(
        string? clientId, string? code, string? redirectUri, string? codeVerifier, CancellationToken cancellationToken);

    Task<(TokenResponse? Tokens, OAuthError? Error)> RefreshAsync(string? clientId, string? refreshToken, CancellationToken cancellationToken);

    Task<int> DeleteExpiredAsync(CancellationToken cancellationToken);
}

internal sealed class OAuthService(
    MCPalDbContext db,
    TimeProvider timeProvider,
    IOptions<McpalOptions> options) : IOAuthService, IAccessTokenValidator
{
    public const string SupportedScope = "mcp";

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public string ResourceUrl => options.Value.PublicUrl.TrimEnd('/') + "/mcp";

    public async Task<OAuthClient> RegisterClientAsync(string? clientName, IReadOnlyList<string> redirectUris, CancellationToken cancellationToken)
    {
        var client = new OAuthClient
        {
            ClientId = NewToken(16),
            ClientName = string.IsNullOrWhiteSpace(clientName) ? "MCP client" : clientName.Trim()[..Math.Min(clientName.Trim().Length, 200)],
            RedirectUris = [.. redirectUris],
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.OAuthClients.Add(client);
        await db.SaveChangesAsync(cancellationToken);
        return client;
    }

    public async Task<AuthorizeValidation> ValidateAuthorizeAsync(AuthorizeParameters parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (string.IsNullOrWhiteSpace(parameters.ClientId))
        {
            return Fail("invalid_request", "client_id is required.");
        }

        var client = await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == parameters.ClientId, cancellationToken);
        if (client is null)
        {
            return Fail("invalid_client", "Unknown client_id.");
        }

        if (string.IsNullOrWhiteSpace(parameters.RedirectUri) || !RedirectUriPolicy.Matches(client.RedirectUris, parameters.RedirectUri))
        {
            return Fail("invalid_request", "redirect_uri does not match a registered redirect URI.");
        }

        var redirect = parameters.RedirectUri;
        if (parameters.ResponseType != "code")
        {
            return Redirectable("unsupported_response_type", "Only response_type=code is supported.");
        }

        if (string.IsNullOrWhiteSpace(parameters.CodeChallenge) || parameters.CodeChallengeMethod != "S256")
        {
            return Redirectable("invalid_request", "PKCE with code_challenge_method=S256 is required.");
        }

        if (parameters.Resource is not null && !string.Equals(parameters.Resource.TrimEnd('/'), ResourceUrl, StringComparison.Ordinal))
        {
            return Redirectable("invalid_target", $"resource must be {ResourceUrl}.");
        }

        var request = new AuthorizeRequest(client, redirect, parameters.CodeChallenge, parameters.State, SupportedScope, parameters.Resource);
        return new AuthorizeValidation(request, null, redirect, parameters.State);

        AuthorizeValidation Fail(string error, string description) => new(null, new OAuthError(error, description), null, null);

        AuthorizeValidation Redirectable(string error, string description) =>
            new(null, new OAuthError(error, description), redirect, parameters.State);
    }

    public async Task<string> IssueCodeAsync(AuthorizeRequest request, Guid companyId, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(userId);

        var code = NewToken(32);
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            CodeHash = ApiKeyService.Hash(code),
            ClientId = request.Client.ClientId,
            CompanyId = companyId,
            UserId = userId,
            RedirectUri = request.RedirectUri,
            CodeChallenge = request.CodeChallenge,
            Scope = request.Scope,
            Resource = request.Resource,
            ExpiresAt = timeProvider.GetUtcNow().Add(CodeLifetime),
        });
        await db.SaveChangesAsync(cancellationToken);
        return code;
    }

    public async Task<(TokenResponse? Tokens, OAuthError? Error)> ExchangeCodeAsync(
        string? clientId, string? code, string? redirectUri, string? codeVerifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri) || string.IsNullOrEmpty(codeVerifier))
        {
            return (null, new OAuthError("invalid_request", "client_id, code, redirect_uri and code_verifier are required."));
        }

        var now = timeProvider.GetUtcNow();
        var codeHash = ApiKeyService.Hash(code);
        var stored = await db.AuthorizationCodes.AsNoTracking().FirstOrDefaultAsync(c => c.CodeHash == codeHash, cancellationToken);
        if (stored is null || stored.Used || stored.ExpiresAt <= now || stored.ClientId != clientId || stored.RedirectUri != redirectUri)
        {
            return (null, InvalidGrant());
        }

        if (!IsPkceValid(codeVerifier, stored.CodeChallenge))
        {
            return (null, InvalidGrant());
        }

        // Single use: only one concurrent exchange can flip Used.
        var claimed = await db.AuthorizationCodes
            .Where(c => c.CodeHash == codeHash && !c.Used)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Used, true), cancellationToken);
        if (claimed == 0)
        {
            return (null, InvalidGrant());
        }

        if (!await IsUserActiveAsync(stored.CompanyId, stored.UserId, cancellationToken))
        {
            return (null, InvalidGrant());
        }

        return (await IssueTokensAsync(stored.CompanyId, stored.UserId, stored.ClientId, stored.Scope, cancellationToken), null);
    }

    public async Task<(TokenResponse? Tokens, OAuthError? Error)> RefreshAsync(string? clientId, string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(refreshToken))
        {
            return (null, new OAuthError("invalid_request", "client_id and refresh_token are required."));
        }

        var now = timeProvider.GetUtcNow();
        var hash = ApiKeyService.Hash(refreshToken);
        var stored = await db.OAuthTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Hash == hash && t.Kind == OAuthTokenKind.Refresh, cancellationToken);
        if (stored is null || stored.Revoked || stored.ExpiresAt <= now || stored.ClientId != clientId)
        {
            return (null, InvalidGrant());
        }

        var rotated = await db.OAuthTokens
            .Where(t => t.Hash == hash && !t.Revoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Revoked, true), cancellationToken);
        if (rotated == 0)
        {
            return (null, InvalidGrant());
        }

        if (!await IsUserActiveAsync(stored.CompanyId, stored.UserId, cancellationToken))
        {
            return (null, InvalidGrant());
        }

        return (await IssueTokensAsync(stored.CompanyId, stored.UserId, stored.ClientId, SupportedScope, cancellationToken), null);
    }

    public async Task<ValidatedAccessToken?> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        var hash = ApiKeyService.Hash(accessToken);
        var now = timeProvider.GetUtcNow();
        var activeUsers = db.Users.Active(db.Companies);
        return await db.OAuthTokens.AsNoTracking()
            .Where(t => t.Hash == hash && t.Kind == OAuthTokenKind.Access && !t.Revoked && t.ExpiresAt > now)
            .Where(t => activeUsers.Any(u => u.Id == t.UserId && u.CompanyId == t.CompanyId))
            .Select(t => new ValidatedAccessToken(t.CompanyId, t.UserId, t.ClientId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var codes = await db.AuthorizationCodes.Where(c => c.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var tokens = await db.OAuthTokens.Where(t => t.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        return codes + tokens;
    }

    private async Task<TokenResponse> IssueTokensAsync(Guid companyId, string userId, string clientId, string scope, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var access = NewToken(32);
        var refresh = NewToken(32);
        var accessLifetime = TimeSpan.FromMinutes(options.Value.AccessTokenLifetimeMinutes);
        db.OAuthTokens.Add(new OAuthToken
        {
            Hash = ApiKeyService.Hash(access),
            Kind = OAuthTokenKind.Access,
            CompanyId = companyId,
            UserId = userId,
            ClientId = clientId,
            ExpiresAt = now.Add(accessLifetime),
        });
        db.OAuthTokens.Add(new OAuthToken
        {
            Hash = ApiKeyService.Hash(refresh),
            Kind = OAuthTokenKind.Refresh,
            CompanyId = companyId,
            UserId = userId,
            ClientId = clientId,
            ExpiresAt = now.AddDays(options.Value.RefreshTokenLifetimeDays),
        });
        await db.SaveChangesAsync(cancellationToken);
        return new TokenResponse(access, refresh, (int)accessLifetime.TotalSeconds, scope);
    }

    private async Task<bool> IsUserActiveAsync(Guid companyId, string userId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().Active(db.Companies).AnyAsync(u => u.Id == userId && u.CompanyId == companyId, cancellationToken);

    private static OAuthError InvalidGrant() => new("invalid_grant", "The authorization grant is invalid, expired or already used.");

    private static bool IsPkceValid(string verifier, string challenge)
    {
        if (verifier.Length is < 43 or > 128)
        {
            return false;
        }

        var computed = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    private static string NewToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
