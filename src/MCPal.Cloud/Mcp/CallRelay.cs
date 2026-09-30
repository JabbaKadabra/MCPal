using System.Text.Json;
using MCPal.Cloud.Tunnel;
using MCPal.Contracts;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace MCPal.Cloud.Mcp;

/// <summary>Forwards a tool call of one company to the agent connection that owns the tool.</summary>
internal sealed class CallRelay(
    ConnectionRegistry registry,
    IAgentInvoker invoker,
    IOptions<McpalOptions> options,
    ILogger<CallRelay> logger)
{
    public async Task<CallToolResult> CallAsync(
        Guid companyId,
        string publicName,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (!registry.TryResolve(companyId, publicName, out var tool))
        {
            return Error($"Tool '{publicName}' is not available (agent offline or unknown tool).");
        }

        var timeout = TimeSpan.FromSeconds(options.Value.ToolCallTimeoutSeconds);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var request = new CallToolRequest(
            Guid.NewGuid().ToString("N"),
            tool.ServerName,
            tool.Tool.Name,
            JsonSerializer.Serialize(arguments ?? new Dictionary<string, JsonElement>(), McpJsonUtilities.DefaultOptions));
        try
        {
            var response = await invoker.CallToolAsync(tool.ConnectionId, request, timeoutSource.Token);
            return Map(response);
        }
        catch (Exception) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Tool call {Tool} of company {CompanyId} timed out after {Seconds}s", publicName, companyId, timeout.TotalSeconds);
            return Error($"Tool call timed out after {timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Tool call {Tool} of company {CompanyId} failed on connection {ConnectionId}", publicName, companyId, tool.ConnectionId);
            return Error("Tool call failed: the agent could not be reached or returned an error.");
        }
    }

    private static CallToolResult Map(CallToolResponse response)
    {
        List<ContentBlock> content = [];
        if (!string.IsNullOrWhiteSpace(response.ContentJson))
        {
            content = JsonSerializer.Deserialize<List<ContentBlock>>(response.ContentJson, McpJsonUtilities.DefaultOptions) ?? [];
        }

        if (content.Count == 0 && response.ErrorMessage is not null)
        {
            content = [new TextContentBlock { Text = response.ErrorMessage }];
        }

        return new CallToolResult { IsError = response.IsError, Content = content };
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
