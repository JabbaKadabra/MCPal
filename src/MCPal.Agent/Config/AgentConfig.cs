namespace MCPal.Agent.Config;

internal sealed record CloudConfig(string Url, string ApiKey, string AgentName);

/// <summary>One local MCP server, in the claude_desktop_config / Claude Code <c>mcpServers</c> shape.</summary>
internal sealed record LocalServerConfig(
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Url,
    IReadOnlyDictionary<string, string> Headers);

internal sealed record AgentConfig(CloudConfig Cloud, IReadOnlyDictionary<string, LocalServerConfig> McpServers, int CallTimeoutSeconds)
{
    public const int DefaultCallTimeoutSeconds = 110;
}

internal sealed class AgentConfigException(string message) : Exception(message);
