using MCPal.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace MCPal.Server.Tunnel;

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
