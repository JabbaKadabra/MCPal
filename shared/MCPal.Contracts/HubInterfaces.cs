namespace MCPal.Contracts;

/// <summary>Server to bridge calls. Result-returning invocation over SignalR.</summary>
public interface IBridgeHubClient
{
    Task<CallToolResponse> CallTool(CallToolRequest request);

    /// <summary>
    /// Fire and forget, best effort (protocol 1.1): the MCPal server stopped waiting for the call with this request id.
    /// Unknown ids are ignored, the call may just have finished.
    /// </summary>
    Task CancelCall(string requestId);
}

/// <summary>Bridge to server hub methods.</summary>
public interface IBridgeHubServer
{
    /// <summary>Registers the bridge's catalog. Every call replaces the catalog of this connection.</summary>
    Task<RegisterResult> Register(BridgeCatalog catalog);
}
