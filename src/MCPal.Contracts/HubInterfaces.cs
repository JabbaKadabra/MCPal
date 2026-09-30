namespace MCPal.Contracts;

/// <summary>Cloud to agent calls. Result-returning invocation over SignalR.</summary>
public interface IAgentHubClient
{
    Task<CallToolResponse> CallTool(CallToolRequest request);

    /// <summary>
    /// Fire and forget, best effort (protocol 1.1): the cloud stopped waiting for the call with this request id.
    /// Unknown ids are ignored, the call may just have finished.
    /// </summary>
    Task CancelCall(string requestId);
}

/// <summary>Agent to cloud hub methods.</summary>
public interface IAgentHubServer
{
    /// <summary>Registers the agent's catalog. Every call replaces the catalog of this connection.</summary>
    Task<RegisterResult> Register(AgentCatalog catalog);
}
