namespace MCPal.Server.Tunnel;

/// <summary>One downloadable bridge archive.</summary>
internal sealed record BridgeDownload(string Rid, string Os, string FileName, string Url);

/// <summary>Where to get the bridge. <see cref="Downloads"/> is empty when the version of the latest release is unknown.</summary>
internal sealed record BridgeDownloadSet(IReadOnlyList<BridgeDownload> Downloads, string ReleasesUrl, string? ChecksumsUrl);

/// <summary>Builds the release asset URLs that <c>release.yml</c> and <c>package-bridge.sh</c> produce (<c>mcpal-bridge-&lt;version&gt;-&lt;rid&gt;.tar.gz|zip</c>).</summary>
internal static class BridgeDownloads
{
    private static readonly (string Rid, string Os, string Extension)[] Platforms =
    [
        ("linux-x64", "linux", "tar.gz"),
        ("linux-arm64", "linux", "tar.gz"),
        ("win-x64", "windows", "zip"),
    ];

    /// <param name="releasesBaseUrl">The releases page, e.g. <c>https://github.com/owner/repo/releases</c>.</param>
    /// <param name="version">Version of the newest release (a leading <c>v</c> is ignored). Empty falls back to the latest release page.</param>
    public static BridgeDownloadSet For(string releasesBaseUrl, string? version)
    {
        var baseUrl = releasesBaseUrl.TrimEnd('/');
        var number = version?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(number))
        {
            return new BridgeDownloadSet([], $"{baseUrl}/latest", null);
        }

        var assets = $"{baseUrl}/download/v{number}";
        var downloads = Platforms
            .Select(p =>
            {
                var fileName = $"mcpal-bridge-{number}-{p.Rid}.{p.Extension}";
                return new BridgeDownload(p.Rid, p.Os, fileName, $"{assets}/{fileName}");
            })
            .ToArray();
        return new BridgeDownloadSet(downloads, $"{baseUrl}/tag/v{number}", $"{assets}/sha256sums.txt");
    }
}
