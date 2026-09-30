using MCPal.Contracts;

namespace MCPal.Agent.Local;

/// <summary>Owns the connections to the local MCP servers of this agent.</summary>
internal interface ILocalServerManager
{
    /// <summary>Raised when a local server reports that its tool list changed.</summary>
    event Action? ToolsChanged;

    /// <summary>Starts servers that are not running yet and returns the tools of all servers that work. Failed servers are omitted.</summary>
    Task<IReadOnlyList<ServerCatalog>> ListServersAsync(CancellationToken cancellationToken);

    /// <summary>Tools hidden by <c>includeTools</c> / <c>excludeTools</c> per server, as of the last listing. For <c>check</c>.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> HiddenTools { get; }

    /// <summary>Calls a tool. Every failure becomes an error response, never an exception.</summary>
    Task<CallToolResponse> CallToolAsync(CallToolRequest request, CancellationToken cancellationToken);
}
