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

    /// <summary>
    /// False: this server gets no caller (no <c>_meta</c> entry, no header), e.g. a third-party server that must not see tokens.
    /// The default is true: the caller goes to every server, so servers that do their own rights checks can.
    /// </summary>
    public bool UserContext { get; init; } = true;

    /// <summary>
    /// HTTP servers only: the request header that carries the caller token of the tool call, e.g. <c>Authorization</c> (sent as
    /// <c>Bearer &lt;token&gt;</c>). Null sends no header; stdio servers read the token from <c>_meta</c>.
    /// </summary>
    public string? UserTokenHeader { get; init; }
}

internal sealed record BridgeConfig(McpalConfig Mcpal, IReadOnlyDictionary<string, LocalServerConfig> McpServers, int CallTimeoutSeconds)
{
    public const int DefaultCallTimeoutSeconds = 110;

    /// <summary>Path of the JSON status file for monitoring tools; null disables it.</summary>
    public string? StatusFile { get; init; }

    /// <summary>Path of the copy of the MCPal server's JWKS that local servers without internet access verify caller tokens with; null disables it.</summary>
    public string? JwksFile { get; init; }
}

internal sealed class BridgeConfigException(string message) : Exception(message);
