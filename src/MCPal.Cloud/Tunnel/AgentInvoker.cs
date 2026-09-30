using MCPal.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace MCPal.Cloud.Tunnel;

/// <summary>Sends tool calls to one agent connection and awaits the result.</summary>
internal interface IAgentInvoker
{
    Task<CallToolResponse> CallToolAsync(string connectionId, CallToolRequest request, CancellationToken cancellationToken);
}

internal sealed class HubAgentInvoker(IHubContext<AgentHub> hub) : IAgentInvoker
{
    public async Task<CallToolResponse> CallToolAsync(string connectionId, CallToolRequest request, CancellationToken cancellationToken)
    {
        return await hub.Clients.Client(connectionId).InvokeAsync<CallToolResponse>(nameof(IAgentHubClient.CallTool), request, cancellationToken);
    }
}
