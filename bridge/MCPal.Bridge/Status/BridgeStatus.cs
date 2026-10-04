using System.Text.Json;

namespace MCPal.Bridge.Status;

internal enum TunnelState
{
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
}

internal sealed record ServerStatus(string Name, string State, int Tools);

internal sealed record RejectedStatus(string Server, string Reason);

/// <summary>The content of the status file: what monitoring tools on the bridge's machine read.</summary>
internal sealed record BridgeStatus(
    DateTimeOffset UpdatedAt,
    string Tunnel,
    string ServerUrl,
    DateTimeOffset? LastRegisteredAt,
    IReadOnlyList<ServerStatus> Servers,
    IReadOnlyList<RejectedStatus> Rejected);

internal static class BridgeStatusFile
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Reads the status file. Throws <see cref="Config.BridgeConfigException"/> with a readable message when it is missing or invalid.</summary>
    public static BridgeStatus Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return JsonSerializer.Deserialize<BridgeStatus>(File.ReadAllText(path), JsonOptions)
                ?? throw new Config.BridgeConfigException($"Status file '{path}' is empty.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Config.BridgeConfigException($"Cannot read status file '{path}': {ex.Message}");
        }
        catch (JsonException ex)
        {
            throw new Config.BridgeConfigException($"Status file '{path}' is not valid: {ex.Message}");
        }
    }
}
