namespace MCPal.Contracts;

/// <summary>
/// A tool exposed by a local MCP server. Schemas travel as JSON strings so Contracts stays SDK-free.
/// <paramref name="OutputSchemaJson"/> (protocol 1.1) is the tool's <c>outputSchema</c>, null when it declares none.
/// </summary>
public sealed record ToolDescriptor(string Name, string? Title, string? Description, string InputSchemaJson, string? AnnotationsJson, string? OutputSchemaJson = null);

public sealed record ServerCatalog(string Name, IReadOnlyList<ToolDescriptor> Tools);

public sealed record BridgeCatalog(string BridgeName, string BridgeVersion, string ProtocolVersion, IReadOnlyList<ServerCatalog> Servers);

public sealed record RejectedServer(string ServerName, string Reason);

/// <summary>A tool of an accepted server that the MCPal server does not expose, e.g. because its schema is invalid or its public name is taken.</summary>
public sealed record RejectedTool(string ServerName, string ToolName, string Reason);

/// <param name="Code">
/// Machine-readable reason when <paramref name="Accepted"/> is false (see <see cref="RegisterCodes"/>); null otherwise and for servers
/// older than protocol 1.1. Lets the bridge react without parsing <paramref name="Message"/>.
/// </param>
public sealed record RegisterResult(
    bool Accepted,
    IReadOnlyList<RejectedServer> RejectedServers,
    IReadOnlyList<RejectedTool> RejectedTools,
    string? Message,
    string? Code = null);

/// <summary>Values of <see cref="RegisterResult.Code"/>.</summary>
public static class RegisterCodes
{
    /// <summary>The bridge's protocol major version differs from the MCPal server's: this bridge is too old or too new for the MCPal server.</summary>
    public const string UnsupportedProtocol = "unsupported_protocol";
}

/// <summary><paramref name="TraceParent"/> (protocol 1.1) is the W3C <c>traceparent</c> of the MCPal server's tool call span, so the bridge's span joins the same trace.</summary>
public sealed record CallToolRequest(string RequestId, string ServerName, string ToolName, string ArgumentsJson, string? TraceParent = null);

/// <summary>
/// <paramref name="ContentJson"/> holds the serialized MCP CallToolResult content array. Protocol 1.1 adds
/// <paramref name="StructuredContentJson"/> (the result's <c>structuredContent</c>) and <paramref name="MetaJson"/> (its <c>_meta</c>).
/// </summary>
public sealed record CallToolResponse(bool IsError, string ContentJson, string? ErrorMessage, string? StructuredContentJson = null, string? MetaJson = null);
