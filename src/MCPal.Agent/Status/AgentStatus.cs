using System.Text.Json;

namespace MCPal.Agent.Status;

internal enum TunnelState
{
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
}

internal sealed record ServerStatus(string Name, string State, int Tools);

internal sealed record RejectedStatus(string Server, string Reason);

/// <summary>The content of the status file: what monitoring tools on the agent's machine read.</summary>
internal sealed record AgentStatus(
    DateTimeOffset UpdatedAt,
    string Tunnel,
    string CloudUrl,
    DateTimeOffset? LastRegisteredAt,
    IReadOnlyList<ServerStatus> Servers,
    IReadOnlyList<RejectedStatus> Rejected);

internal static class AgentStatusFile
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Reads the status file. Throws <see cref="Config.AgentConfigException"/> with a readable message when it is missing or invalid.</summary>
    public static AgentStatus Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return JsonSerializer.Deserialize<AgentStatus>(File.ReadAllText(path), JsonOptions)
                ?? throw new Config.AgentConfigException($"Status file '{path}' is empty.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Config.AgentConfigException($"Cannot read status file '{path}': {ex.Message}");
        }
        catch (JsonException ex)
        {
            throw new Config.AgentConfigException($"Status file '{path}' is not valid: {ex.Message}");
        }
    }
}
