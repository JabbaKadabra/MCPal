namespace MCPal.Contracts;

/// <summary>A tool exposed by a local MCP server. Schema travels as a JSON string so Contracts stays SDK-free.</summary>
public sealed record ToolDescriptor(string Name, string? Title, string? Description, string InputSchemaJson, string? AnnotationsJson);

public sealed record ServerCatalog(string Name, IReadOnlyList<ToolDescriptor> Tools);

public sealed record AgentCatalog(string AgentName, string AgentVersion, string ProtocolVersion, IReadOnlyList<ServerCatalog> Servers);

public sealed record RejectedServer(string ServerName, string Reason);

public sealed record RegisterResult(bool Accepted, IReadOnlyList<RejectedServer> RejectedServers, string? Message);

public sealed record CallToolRequest(string RequestId, string ServerName, string ToolName, string ArgumentsJson);

/// <summary><paramref name="ContentJson"/> holds the serialized MCP CallToolResult content array.</summary>
public sealed record CallToolResponse(bool IsError, string ContentJson, string? ErrorMessage);
