using System.Text.Json;
using MCPal.Server.Ports;
using MCPal.Server.Tenancy;
using MCPal.Server.Tunnel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCPal.Server.Portal;

internal sealed record BridgeDownloadResponse(string Rid, string Os, string FileName, string Url);

internal sealed record SetupResponse(
    string McpalUrl,
    string? BridgeVersion,
    IReadOnlyList<BridgeDownloadResponse> Downloads,
    string ReleasesUrl,
    string? ChecksumsUrl,
    string ImageReference,
    string ConfigJson,
    string SampleMcpJson,
    bool HasConnectedBridge);

/// <summary>What the owner needs to install the first bridge: download links, the image reference, a pre-filled <c>mcpal.json</c> and whether a bridge ever connected.</summary>
internal static class SetupEndpoints
{
    /// <summary>
    /// A first <c>mcp.json</c> for owners without one: the reference "everything" server, reduced to its <c>echo</c> tool (the server also
    /// has tools that print the environment). Keep in sync with <c>bridge/MCPal.Bridge/mcp.example.json</c>.
    /// </summary>
    private const string SampleMcpJson = """
        {
          "mcpServers": {
            "everything": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-everything", "stdio"],
              "includeTools": ["echo"]
            }
          }
        }
        """;

    private static readonly JsonSerializerOptions ConfigFormat = new() { WriteIndented = true };

    public static void Map(RouteGroupBuilder secured)
    {
        ArgumentNullException.ThrowIfNull(secured);

        secured.MapGroup(string.Empty).AddEndpointFilter<OwnerOnlyFilter>().MapGet("setup", SetupAsync);
    }

    private static async Task<IResult> SetupAsync(System.Security.Claims.ClaimsPrincipal principal, UserManager<PortalUser> users, ConnectionRegistry registry, IMcpalData db, IOptions<McpalOptions> options, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        var settings = options.Value;
        var version = string.IsNullOrWhiteSpace(settings.LatestBridgeVersion) ? null : settings.LatestBridgeVersion.Trim();
        var downloads = BridgeDownloads.For(settings.BridgeReleaseBaseUrl, settings.BridgeImage.Trim(), version);
        var baseUrl = settings.PublicUrl.TrimEnd('/');

        // A bridge key that was ever validated means a bridge connected at least once (bridge keys open tunnels only), even if the key was disabled since.
        var connected = registry.Connections(companyId).Count > 0
            || await db.Query<ApiKey>().AsNoTracking().AnyAsync(k => k.CompanyId == companyId && k.Purpose == ApiKeyPurpose.Bridge && k.LastUsedAt != null, cancellationToken);

        return Results.Json(new SetupResponse(
            baseUrl,
            version,
            [.. downloads.Downloads.Select(d => new BridgeDownloadResponse(d.Rid, d.Os, d.FileName, d.Url))],
            downloads.ReleasesUrl,
            downloads.ChecksumsUrl,
            downloads.ImageReference,
            ConfigJson(baseUrl),
            SampleMcpJson,
            connected));
    }

    /// <summary>
    /// Neither the key nor the local servers are part of the file: the key travels as <c>MCPAL_API_KEY</c> (set by the installers) and
    /// the servers live in <c>mcp.json</c> next to it, so the file is safe to save and share.
    /// </summary>
    private static string ConfigJson(string mcpalUrl) =>
        JsonSerializer.Serialize(new { mcpal = new { url = mcpalUrl } }, ConfigFormat);
}
