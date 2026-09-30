namespace MCPal.Bridge.Config;

internal sealed record McpalConfig(string Url, string ApiKey, string BridgeName);

/// <summary>One local MCP server, in the claude_desktop_config / Claude Code <c>mcpServers</c> shape.</summary>
internal sealed record LocalServerConfig(
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Url,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Glob patterns (<c>*</c>, <c>?</c>; case-sensitive). When not empty, only matching tools are exposed.</summary>
    public IReadOnlyList<string> IncludeTools { get; init; } = [];

    /// <summary>Glob patterns of tools that are never exposed, applied after <see cref="IncludeTools"/>.</summary>
    public IReadOnlyList<string> ExcludeTools { get; init; } = [];
}

internal sealed record BridgeConfig(McpalConfig Mcpal, IReadOnlyDictionary<string, LocalServerConfig> McpServers, int CallTimeoutSeconds)
{
    public const int DefaultCallTimeoutSeconds = 110;

    /// <summary>Path of the JSON status file for monitoring tools; null disables it.</summary>
    public string? StatusFile { get; init; }
}

internal sealed class BridgeConfigException(string message) : Exception(message);
