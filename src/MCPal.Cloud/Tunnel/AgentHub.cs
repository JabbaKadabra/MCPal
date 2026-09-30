using MCPal.Cloud.Tenancy;
using MCPal.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MCPal.Cloud.Tunnel;

/// <summary>
/// Tunnel endpoint for agents. Company and API key come from the authenticated principal, never from the payload.
/// </summary>
[Authorize(AuthenticationSchemes = McpBearerDefaults.Scheme, Policy = McpBearerDefaults.TunnelPolicy)]
internal sealed class AgentHub(ConnectionRegistry registry, TimeProvider timeProvider, ILogger<AgentHub> logger) : Hub, IAgentHubServer
{
    public override Task OnConnectedAsync()
    {
        var (companyId, apiKeyId) = Identify();
        registry.Add(companyId, Context.ConnectionId, apiKeyId, timeProvider.GetUtcNow(), Context.Abort);
        logger.LogInformation("Agent tunnel connected: company {CompanyId}, connection {ConnectionId}", companyId, Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var (companyId, _) = Identify();
        registry.Remove(companyId, Context.ConnectionId);
        logger.LogInformation("Agent tunnel disconnected: company {CompanyId}, connection {ConnectionId}", companyId, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task<RegisterResult> Register(AgentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var (companyId, _) = Identify();
        if (!IsSupported(catalog.ProtocolVersion))
        {
            return Task.FromResult(new RegisterResult(
                false,
                [],
                $"Unsupported protocol version '{catalog.ProtocolVersion}'. This cloud speaks protocol major {ProtocolVersion.Major}."));
        }

        var result = registry.Register(companyId, Context.ConnectionId, catalog);
        foreach (var rejected in result.RejectedServers)
        {
            logger.LogWarning("Server '{Server}' of company {CompanyId} rejected: {Reason}", rejected.ServerName, companyId, rejected.Reason);
        }

        return Task.FromResult(result);
    }

    public Task ToolsChanged(AgentCatalog catalog)
    {
        Register(catalog);
        return Task.CompletedTask;
    }

    private (Guid CompanyId, Guid ApiKeyId) Identify()
    {
        var user = Context.User ?? throw new HubException("Not authenticated.");
        var companyId = user.GetCompanyId() ?? throw new HubException("Not authenticated.");
        var apiKeyId = user.GetApiKeyId() ?? throw new HubException("Tunnels require an API key.");
        return (companyId, apiKeyId);
    }

    private static bool IsSupported(string version)
    {
        var major = version.Split('.')[0];
        return int.TryParse(major, out var value) && value == ProtocolVersion.Major;
    }
}
