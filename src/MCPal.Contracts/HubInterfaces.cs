namespace MCPal.Contracts;

/// <summary>Cloud to agent calls. Result-returning invocation over SignalR.</summary>
public interface IAgentHubClient
{
    Task<CallToolResponse> CallTool(CallToolRequest request);
}

/// <summary>Agent to cloud hub methods.</summary>
public interface IAgentHubServer
{
    /// <summary>Registers the agent's catalog. Every call replaces the catalog of this connection.</summary>
    Task<RegisterResult> Register(AgentCatalog catalog);
}
