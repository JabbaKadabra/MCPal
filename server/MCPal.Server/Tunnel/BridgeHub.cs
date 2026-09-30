using MCPal.Server.Diagnostics;
using MCPal.Server.Tenancy;
using MCPal.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MCPal.Server.Tunnel;

/// <summary>
/// Tunnel endpoint for bridges. Company and API key come from the authenticated principal, never from the payload.
/// </summary>
[Authorize(AuthenticationSchemes = McpBearerDefaults.Scheme, Policy = McpBearerDefaults.TunnelPolicy)]
internal sealed class BridgeHub(ConnectionRegistry registry, IApiKeyService apiKeys, ServerTelemetry telemetry, TimeProvider timeProvider, ILogger<BridgeHub> logger) : Hub, IBridgeHubServer
{
    public override async Task OnConnectedAsync()
    {
        var (companyId, apiKeyId) = Identify();
        registry.Add(companyId, Context.ConnectionId, apiKeyId, timeProvider.GetUtcNow(), Context.Abort);

        // A revoke between authentication and Add found no tunnel to close. Checking after Add closes that gap:
        // a later revoke finds the tunnel in the registry, an earlier one is seen here.
        if (!await apiKeys.IsActiveAsync(companyId, apiKeyId, Context.ConnectionAborted))
        {
            registry.Remove(companyId, Context.ConnectionId);
            logger.LogInformation("Bridge tunnel refused: key {ApiKeyId} of company {CompanyId} is no longer active", apiKeyId, companyId);
            Context.Abort();
            return;
        }

        logger.LogInformation("Bridge tunnel connected: company {CompanyId}, connection {ConnectionId}", companyId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var (companyId, _) = Identify();
        registry.Remove(companyId, Context.ConnectionId);
        logger.LogInformation("Bridge tunnel disconnected: company {CompanyId}, connection {ConnectionId}", companyId, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task<RegisterResult> Register(BridgeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var (companyId, _) = Identify();
        if (!IsSupported(catalog.ProtocolVersion))
        {
            var unsupported = new RegisterResult(
                false,
                [],
                [],
                $"Unsupported protocol version '{catalog.ProtocolVersion}'. This server speaks protocol major {ProtocolVersion.Major}.",
                RegisterCodes.UnsupportedProtocol);
            telemetry.RecordRegistration(unsupported);
            return Task.FromResult(unsupported);
        }

        var result = registry.Register(companyId, Context.ConnectionId, catalog);
        telemetry.RecordRegistration(result);
        foreach (var rejected in result.RejectedServers)
        {
            logger.LogWarning("Server '{Server}' of company {CompanyId} rejected: {Reason}", rejected.ServerName, companyId, rejected.Reason);
        }

        foreach (var rejected in result.RejectedTools)
        {
            logger.LogWarning("Tool '{Tool}' of server '{Server}' of company {CompanyId} rejected: {Reason}", rejected.ToolName, rejected.ServerName, companyId, rejected.Reason);
        }

        return Task.FromResult(result);
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
