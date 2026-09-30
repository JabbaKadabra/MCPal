using MCPal.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace MCPal.Cloud.Tunnel;

/// <summary>Sends tool calls to one agent connection and awaits the result.</summary>
internal interface IAgentInvoker
{
    Task<CallToolResponse> CallToolAsync(string connectionId, CallToolRequest request, CancellationToken cancellationToken);

    /// <summary>Tells the agent that the cloud stopped waiting for a call. Best effort, only for agents of protocol 1.1 and later.</summary>
    Task CancelCallAsync(string connectionId, string requestId, CancellationToken cancellationToken);
}

internal sealed class HubAgentInvoker(IHubContext<AgentHub> hub) : IAgentInvoker
{
    public async Task<CallToolResponse> CallToolAsync(string connectionId, CallToolRequest request, CancellationToken cancellationToken)
    {
        return await hub.Clients.Client(connectionId).InvokeAsync<CallToolResponse>(nameof(IAgentHubClient.CallTool), request, cancellationToken);
    }

    public async Task CancelCallAsync(string connectionId, string requestId, CancellationToken cancellationToken)
    {
        await hub.Clients.Client(connectionId).SendAsync(nameof(IAgentHubClient.CancelCall), requestId, cancellationToken);
    }
}
