using System.Text.Json;
using System.Text.Json.Nodes;
using MCPal.Server.Portal;

namespace MCPal.Server.Access.UserContext;

/// <summary>
/// <c>GET /.well-known/jwks.json</c>: the public keys that verify caller tokens (RFC 7517). Anonymous and cacheable: bridges and
/// local MCP servers fetch it. It lists the signing key, the pre-published successor and retired keys in their grace period.
/// </summary>
internal static class JwksEndpoints
{
    public const string Path = "/.well-known/jwks.json";
    public const int CacheSeconds = 300;

    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Path, GetAsync).RequireRateLimiting(PortalEndpoints.RateLimitPolicy);
    }

    private static async Task<IResult> GetAsync(SigningKeyStore keys, HttpContext http, CancellationToken cancellationToken)
    {
        var published = await keys.GetPublishedKeysAsync(cancellationToken);
        var array = new JsonArray();
        foreach (var key in published)
        {
            // The stored JWK holds the public members only; there is no code path that puts a private member into it.
            array.Add(JsonNode.Parse(key.PublicJwkJson));
        }

        http.Response.Headers.CacheControl = $"public, max-age={CacheSeconds}";
        return Results.Text(new JsonObject { ["keys"] = array }.ToJsonString(new JsonSerializerOptions()), "application/json");
    }
}
