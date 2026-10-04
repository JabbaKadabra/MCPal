using MCPal.Server.Access;
using MCPal.Server.Tenancy;
using MCPal.Server.Tunnel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MCPal.Server.Mcp;

/// <summary>Per-request MCP handlers. They serve only the tools of the company found in the authenticated principal.</summary>
internal sealed class TenantToolHandlers(ConnectionRegistry registry, CallRelay relay, AccessEvaluator access)
{
    public const string Instructions =
        "Tools are grouped by the MCP server of the connected company. Tool names have the form 'server__tool'.";

    /// <param name="requestAborted">
    /// Fires when the MCP client drops the HTTP request. The stateless transport does not link it to the handler's token,
    /// so calls are cancelled here and the bridge learns that nobody waits for the result any more.
    /// </param>
    public void Configure(McpServerOptions options, CallerIdentity caller, CancellationToken requestAborted)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(caller);

        options.ServerInfo = new Implementation { Name = "MCPal", Version = "1.0.0" };
        options.ServerInstructions = Instructions;
        options.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } };
        options.Handlers.ListToolsHandler = async (_, cancellationToken) => await ListToolsAsync(caller, cancellationToken);
        options.Handlers.CallToolHandler = async (context, cancellationToken) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestAborted);
            return await relay.CallAsync(caller, context.Params?.Name ?? string.Empty, context.Params?.Arguments, linked.Token);
        };
    }

    /// <summary>Only the tools the caller may use. A forbidden tool is not listed at all, like a tool that does not exist.</summary>
    private async ValueTask<ListToolsResult> ListToolsAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        var policy = await access.GetUserPolicyAsync(caller.CompanyId, caller.UserId, cancellationToken);
        return new ListToolsResult
        {
            Tools = policy is null
                ? []
                : [.. registry.Tools(caller.CompanyId).Where(registered => policy.Allows(registered.ServerName, registered.Descriptor.Name)).Select(registered => registered.Listing)],
        };
    }
}
