using MCPal.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace MCPal.Server.Tunnel;

/// <summary>Sends tool calls to one bridge connection and awaits the result.</summary>
internal interface IBridgeInvoker
{
    Task<CallToolResponse> CallToolAsync(string connectionId, CallToolRequest request, CancellationToken cancellationToken);

    /// <summary>Tells the bridge that the server stopped waiting for a call. Best effort, only for bridges of protocol 1.1 and later.</summary>
    Task CancelCallAsync(string connectionId, string requestId, CancellationToken cancellationToken);
}

internal sealed class HubBridgeInvoker(IHubContext<BridgeHub> hub) : IBridgeInvoker
{
    public async Task<CallToolResponse> CallToolAsync(string connectionId, CallToolRequest request, CancellationToken cancellationToken)
    {
        return await hub.Clients.Client(connectionId).InvokeAsync<CallToolResponse>(nameof(IBridgeHubClient.CallTool), request, cancellationToken);
    }

    public async Task CancelCallAsync(string connectionId, string requestId, CancellationToken cancellationToken)
    {
        await hub.Clients.Client(connectionId).SendAsync(nameof(IBridgeHubClient.CancelCall), requestId, cancellationToken);
    }
}
