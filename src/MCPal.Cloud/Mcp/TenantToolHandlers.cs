using MCPal.Cloud.Tenancy;
using MCPal.Cloud.Tunnel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MCPal.Cloud.Mcp;

/// <summary>Per-request MCP handlers. They serve only the tools of the company found in the authenticated principal.</summary>
internal sealed class TenantToolHandlers(ConnectionRegistry registry, CallRelay relay)
{
    public const string Instructions =
        "Tools are grouped by the MCP server of the connected company. Tool names have the form 'server__tool'.";

    /// <param name="requestAborted">
    /// Fires when the MCP client drops the HTTP request. The stateless transport does not link it to the handler's token,
    /// so calls are cancelled here and the agent learns that nobody waits for the result any more.
    /// </param>
    public void Configure(McpServerOptions options, CallerIdentity caller, CancellationToken requestAborted)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(caller);

        options.ServerInfo = new Implementation { Name = "MCPal", Version = "1.0.0" };
        options.ServerInstructions = Instructions;
        options.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } };
        options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(ListTools(caller));
        options.Handlers.CallToolHandler = async (context, cancellationToken) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestAborted);
            return await relay.CallAsync(caller, context.Params?.Name ?? string.Empty, context.Params?.Arguments, linked.Token);
        };
    }

    private ListToolsResult ListTools(CallerIdentity caller)
    {
        return new ListToolsResult { Tools = [.. registry.Tools(caller.CompanyId).Where(registered => caller.Allows(registered.ServerName)).Select(registered => registered.Listing)] };
    }
}
