using System.Text.Json;
using MCPal.Bridge.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCPal.Bridge.Tunnel;

/// <summary>
/// Keeps a copy of the MCPal server's JWKS (<c>&lt;url&gt;/.well-known/jwks.json</c>) in the file configured as <c>jwksFile</c>, so local
/// MCP servers without internet access can verify caller tokens. Fetches at startup and then hourly through the bridge's own
/// outbound path, and replaces the file atomically (temp file in the same directory, then rename), so readers never see a
/// half-written file. A failed fetch keeps the old file.
/// </summary>
internal sealed class JwksFileWriter : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    private readonly BridgeConfig config;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<JwksFileWriter> logger;
    private readonly HttpMessageHandler? handler;

    /// <param name="handler">Null in production; tests (and in-memory hosts) bring their own handler.</param>
    public JwksFileWriter(BridgeConfig config, TimeProvider timeProvider, ILogger<JwksFileWriter> logger, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.config = config;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.handler = handler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config.JwksFile is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await FetchOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Could not refresh the JWKS file '{File}': {Message}. The previous file stays in place.", config.JwksFile, ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Fetches the JWKS once and replaces the file. Throws when the fetch fails or the answer is not a JWKS; the file is untouched then.</summary>
    internal async Task FetchOnceAsync(CancellationToken cancellationToken)
    {
        var file = config.JwksFile ?? throw new InvalidOperationException("No jwksFile is configured.");
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = FetchTimeout;
        var json = await client.GetStringAsync(new Uri(config.Mcpal.Url + "/.well-known/jwks.json"), cancellationToken);
        EnsureJwks(json);

        var path = Path.GetFullPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        logger.LogInformation("JWKS file '{File}' updated", path);
    }

    private static void EnsureJwks(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("keys", out var keys)
                || keys.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The answer is not a JWKS (no 'keys' array).");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The answer is not valid JSON: " + ex.Message, ex);
        }
    }
}
