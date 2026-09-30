using System.Text.Json;
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

    public void Configure(McpServerOptions options, Guid companyId)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ServerInfo = new Implementation { Name = "MCPal", Version = "1.0.0" };
        options.ServerInstructions = Instructions;
        options.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } };
        options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(ListTools(companyId));
        options.Handlers.CallToolHandler = async (context, cancellationToken) =>
            await relay.CallAsync(companyId, context.Params?.Name ?? string.Empty, context.Params?.Arguments, cancellationToken);
    }

    private ListToolsResult ListTools(Guid companyId)
    {
        var tools = registry.Tools(companyId).Select(registered =>
        {
            var descriptor = registered.Tool;
            return new Tool
            {
                Name = registered.PublicName,
                Title = descriptor.Title,
                Description = $"[{registered.ServerName}] {descriptor.Description}".TrimEnd(),
                InputSchema = JsonSerializer.Deserialize<JsonElement>(descriptor.InputSchemaJson),
                Annotations = descriptor.AnnotationsJson is null
                    ? null
                    : JsonSerializer.Deserialize<ToolAnnotations>(descriptor.AnnotationsJson, McpJsonUtilities.DefaultOptions),
            };
        });
        return new ListToolsResult { Tools = [.. tools] };
    }
}
