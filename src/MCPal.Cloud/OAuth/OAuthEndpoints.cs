using System.Text.Json.Serialization;
using MCPal.Cloud.Tenancy;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MCPal.Cloud.OAuth;

internal sealed record ClientRegistrationRequest(
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

internal sealed record ClientRegistrationResponse(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_name")] string ClientName,
    [property: JsonPropertyName("redirect_uris")] string[] RedirectUris,
    [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod,
    [property: JsonPropertyName("grant_types")] string[] GrantTypes,
    [property: JsonPropertyName("response_types")] string[] ResponseTypes,
    [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt);

internal sealed record AuthorizeContextResponse(string ClientName, string RedirectHost, string? SignedInCompany);

internal sealed record AuthorizeSubmitRequest(
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("redirect_uri")] string? RedirectUri,
    [property: JsonPropertyName("response_type")] string? ResponseType,
    [property: JsonPropertyName("code_challenge")] string? CodeChallenge,
    [property: JsonPropertyName("code_challenge_method")] string? CodeChallengeMethod,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("resource")] string? Resource,
    [property: JsonPropertyName("api_key")] string? ApiKey,
    [property: JsonPropertyName("use_session")] bool UseSession);

internal sealed record RedirectResponse([property: JsonPropertyName("redirectUrl")] string RedirectUrl);

internal static class OAuthEndpoints
{
    private static readonly string[] Scopes = [OAuthService.SupportedScope];
    private static readonly string[] BearerMethods = ["header"];
    private static readonly string[] ResponseTypes = ["code"];
    private static readonly string[] GrantTypes = ["authorization_code", "refresh_token"];
    private static readonly string[] ChallengeMethods = ["S256"];
    private static readonly string[] AuthMethods = ["none"];

    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResourceMetadata);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", ProtectedResourceMetadata);
        app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServerMetadata);
        app.MapPost("/oauth/register", RegisterAsync);
        app.MapPost("/oauth/token", TokenAsync);
        app.MapGet("/api/oauth/authorize/context", AuthorizeContextAsync);
        app.MapPost("/api/oauth/authorize", AuthorizeAsync);
    }

    private static IResult ProtectedResourceMetadata(IOptions<McpalOptions> options)
    {
        var baseUrl = options.Value.PublicUrl.TrimEnd('/');
        return Results.Json(new Dictionary<string, object>
        {
            ["resource"] = baseUrl + "/mcp",
            ["authorization_servers"] = new[] { baseUrl },
            ["scopes_supported"] = Scopes,
            ["bearer_methods_supported"] = BearerMethods,
        });
    }

    private static IResult AuthorizationServerMetadata(IOptions<McpalOptions> options)
    {
        var baseUrl = options.Value.PublicUrl.TrimEnd('/');
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = baseUrl,
            ["authorization_endpoint"] = baseUrl + "/oauth/authorize",
            ["token_endpoint"] = baseUrl + "/oauth/token",
            ["registration_endpoint"] = baseUrl + "/oauth/register",
            ["response_types_supported"] = ResponseTypes,
            ["grant_types_supported"] = GrantTypes,
            ["code_challenge_methods_supported"] = ChallengeMethods,
            ["token_endpoint_auth_methods_supported"] = AuthMethods,
            ["scopes_supported"] = Scopes,
        });
    }

    private static async Task<IResult> RegisterAsync(ClientRegistrationRequest? request, IOAuthService oauth, TimeProvider time, CancellationToken cancellationToken)
    {
        if (request?.RedirectUris is not { Length: > 0 } redirectUris)
        {
            return Error(400, "invalid_client_metadata", "redirect_uris is required.");
        }

        if (request.TokenEndpointAuthMethod is not (null or "none"))
        {
            return Error(400, "invalid_client_metadata", "Only token_endpoint_auth_method=none is supported.");
        }

        var rejected = redirectUris.FirstOrDefault(uri => !RedirectUriPolicy.IsAllowedForRegistration(uri));
        if (rejected is not null)
        {
            return Error(400, "invalid_redirect_uri", $"Redirect URI is not allowed: {rejected}");
        }

        var client = await oauth.RegisterClientAsync(request.ClientName, redirectUris, cancellationToken);
        return Results.Json(
            new ClientRegistrationResponse(
                client.ClientId,
                client.ClientName,
                client.RedirectUris,
                "none",
                GrantTypes,
                ResponseTypes,
                client.CreatedAt.ToUnixTimeSeconds()),
            statusCode: 201);
    }

    private static async Task<IResult> TokenAsync(HttpRequest request, IOAuthService oauth, CancellationToken cancellationToken)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!request.HasFormContentType)
        {
            return Error(400, "invalid_request", "The token endpoint accepts application/x-www-form-urlencoded only.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var grantType = form["grant_type"].ToString();
        var (tokens, error) = grantType switch
        {
            "authorization_code" => await oauth.ExchangeCodeAsync(
                form["client_id"], form["code"], form["redirect_uri"], form["code_verifier"], cancellationToken),
            "refresh_token" => await oauth.RefreshAsync(form["client_id"], form["refresh_token"], cancellationToken),
            _ => (null, new OAuthError("unsupported_grant_type", "grant_type must be authorization_code or refresh_token.")),
        };
        if (tokens is null)
        {
            return Error(400, error?.Error ?? "invalid_request", error?.Description ?? "Invalid request.");
        }

        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = tokens.AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = tokens.ExpiresIn,
            ["refresh_token"] = tokens.RefreshToken,
            ["scope"] = tokens.Scope,
        });
    }

    private static async Task<IResult> AuthorizeContextAsync(
        HttpRequest request, IOAuthService oauth, UserManager<PortalUser> users, ICompanyService companies, CancellationToken cancellationToken)
    {
        var query = request.Query;
        var parameters = new AuthorizeParameters(
            query["client_id"], query["redirect_uri"], query["response_type"], query["code_challenge"],
            query["code_challenge_method"], query["state"], query["scope"], query["resource"]);
        var validation = await oauth.ValidateAuthorizeAsync(parameters, cancellationToken);
        if (validation.Request is not { } authorize)
        {
            return Error(400, validation.Error?.Error ?? "invalid_request", validation.Error?.Description ?? "Invalid request.");
        }

        var signedIn = await SessionCompanyAsync(request.HttpContext, users, companies, cancellationToken);
        return Results.Json(new AuthorizeContextResponse(authorize.Client.ClientName, new Uri(authorize.RedirectUri).Host, signedIn?.Name));
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http,
        AuthorizeSubmitRequest? body,
        IOAuthService oauth,
        IApiKeyService apiKeys,
        UserManager<PortalUser> users,
        ICompanyService companies,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return Error(400, "invalid_request", "Request body is required.");
        }

        var parameters = new AuthorizeParameters(
            body.ClientId, body.RedirectUri, body.ResponseType, body.CodeChallenge, body.CodeChallengeMethod, body.State, body.Scope, body.Resource);
        var validation = await oauth.ValidateAuthorizeAsync(parameters, cancellationToken);
        if (validation.CanRedirect && validation.RedirectUri is { } errorRedirect)
        {
            return Results.Json(new RedirectResponse(BuildRedirect(errorRedirect, validation.State, error: validation.Error)));
        }

        if (validation.Request is not { } authorize)
        {
            return Error(400, validation.Error?.Error ?? "invalid_request", validation.Error?.Description ?? "Invalid request.");
        }

        Guid companyId;
        Guid? apiKeyId = null;
        if (body.UseSession)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Error(400, "invalid_request", "Invalid or missing anti-forgery token.");
            }

            if (await SessionCompanyAsync(http, users, companies, cancellationToken) is not { } company)
            {
                return Error(401, "login_required", "Sign in to the portal first.");
            }

            companyId = company.Id;
        }
        else
        {
            var key = string.IsNullOrWhiteSpace(body.ApiKey) ? null : await apiKeys.ValidateAsync(body.ApiKey.Trim(), cancellationToken);
            if (key is null)
            {
                return Error(401, "invalid_key", "The API key is missing, invalid, expired or revoked.");
            }

            companyId = key.CompanyId;
            apiKeyId = key.ApiKeyId;
        }

        var code = await oauth.IssueCodeAsync(authorize, companyId, apiKeyId, cancellationToken);
        return Results.Json(new RedirectResponse(BuildRedirect(authorize.RedirectUri, authorize.State, code: code)));
    }

    private static async Task<Company?> SessionCompanyAsync(HttpContext http, UserManager<PortalUser> users, ICompanyService companies, CancellationToken cancellationToken)
    {
        var result = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded || await users.GetUserAsync(result.Principal) is not { } user)
        {
            return null;
        }

        var company = await companies.FindAsync(user.CompanyId, cancellationToken);
        return company is { Disabled: false } ? company : null;
    }

    private static string BuildRedirect(string redirectUri, string? state, string? code = null, OAuthError? error = null)
    {
        var builder = new UriBuilder(redirectUri);
        var query = QueryString.FromUriComponent(builder.Query);
        if (code is not null)
        {
            query = query.Add("code", code);
        }

        if (error is not null)
        {
            query = query.Add("error", error.Error).Add("error_description", error.Description);
        }

        if (!string.IsNullOrEmpty(state))
        {
            query = query.Add("state", state);
        }

        builder.Query = query.ToUriComponent().TrimStart('?');
        return builder.Uri.ToString();
    }

    private static IResult Error(int status, string error, string description) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }, statusCode: status);
}
