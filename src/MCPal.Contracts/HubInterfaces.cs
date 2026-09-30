namespace MCPal.Contracts;

/// <summary>Cloud to agent calls. Result-returning invocation over SignalR.</summary>
public interface IAgentHubClient
{
    Task<CallToolResponse> CallTool(CallToolRequest request);
}

/// <summary>Agent to cloud hub methods.</summary>
public interface IAgentHubServer
{
    Task<RegisterResult> Register(AgentCatalog catalog);

    Task ToolsChanged(AgentCatalog catalog);
}
