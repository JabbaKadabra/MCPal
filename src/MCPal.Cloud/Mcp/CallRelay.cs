using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MCPal.Cloud.Audit;
using MCPal.Cloud.Diagnostics;
using MCPal.Cloud.Tenancy;
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
    CloudTelemetry telemetry,
    IAuditSink audit,
    TimeProvider timeProvider,
    IOptions<McpalOptions> options,
    ILogger<CallRelay> logger)
{
    private static readonly TimeSpan CancelSendTimeout = TimeSpan.FromSeconds(5);

    public async Task<CallToolResult> CallAsync(
        CallerIdentity caller,
        string publicName,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var companyId = caller.CompanyId;
        var startedAt = timeProvider.GetUtcNow();
        var started = timeProvider.GetTimestamp();
        // A tool outside the caller's server scope answers exactly like an unknown tool: its existence is not revealed.
        if (!registry.TryResolve(companyId, publicName, out var tool) || !caller.Allows(tool.ServerName))
        {
            var message = $"Tool '{publicName}' is not available (agent offline or unknown tool).";
            using var offline = telemetry.StartToolCall(companyId, string.Empty, publicName);
            Finish(caller, null, publicName, offline, startedAt, started, ToolCallOutcome.Offline, message);
            return Error(message);
        }

        var timeout = TimeSpan.FromSeconds(options.Value.ToolCallTimeoutSeconds);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var activity = telemetry.StartToolCall(companyId, tool.ServerName, tool.Descriptor.Name);
        var request = new CallToolRequest(
            Guid.NewGuid().ToString("N"),
            tool.ServerName,
            tool.Descriptor.Name,
            JsonSerializer.Serialize(arguments ?? new Dictionary<string, JsonElement>(), McpJsonUtilities.DefaultOptions),
            activity?.Id);
        var outcome = ToolCallOutcome.RelayError;
        string? errorMessage = "The agent could not be reached or returned an error.";
        try
        {
            var response = await invoker.CallToolAsync(tool.ConnectionId, request, timeoutSource.Token);
            outcome = ToolCallOutcome.From(response);
            errorMessage = outcome == ToolCallOutcome.Ok ? null : response.ErrorMessage;
            return Map(response, publicName);
        }
        catch (Exception) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            outcome = ToolCallOutcome.Timeout;
            errorMessage = $"Tool call timed out after {timeout.TotalSeconds:0} s.";
            logger.LogWarning("Tool call {Tool} of company {CompanyId} timed out after {Seconds}s", publicName, companyId, timeout.TotalSeconds);
            await CancelAtAgentAsync(tool, request.RequestId);
            return Error(errorMessage);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Tool call {Tool} of company {CompanyId} failed on connection {ConnectionId}", publicName, companyId, tool.ConnectionId);
            await CancelAtAgentAsync(tool, request.RequestId);
            return Error("Tool call failed: the agent could not be reached or returned an error.");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            outcome = ToolCallOutcome.Cancelled;
            errorMessage = "The caller cancelled the call.";
            await CancelAtAgentAsync(tool, request.RequestId);
            throw;
        }
        finally
        {
            Finish(caller, tool, publicName, activity, startedAt, started, outcome, errorMessage);
        }
    }

    /// <summary>The one place where a finished call is classified: metrics, the span and the audit log all get the same outcome.</summary>
    private void Finish(CallerIdentity caller, RegisteredTool? tool, string publicName, Activity? activity, DateTimeOffset startedAt, long started, string outcome, string? errorMessage)
    {
        var elapsed = timeProvider.GetElapsedTime(started);
        telemetry.RecordToolCall(activity, outcome, elapsed);
        audit.Enqueue(new ToolCallAudit
        {
            Id = Guid.CreateVersion7(startedAt),
            CompanyId = caller.CompanyId,
            OccurredAt = startedAt,
            DurationMs = (int)Math.Min(int.MaxValue, elapsed.TotalMilliseconds),
            AuthKind = caller.AuthKind,
            ApiKeyId = caller.ApiKeyId,
            OAuthClientId = caller.OAuthClientId,
            AgentName = tool?.AgentName ?? string.Empty,
            ServerName = tool?.ServerName ?? string.Empty,
            ToolName = tool?.Descriptor.Name ?? publicName,
            PublicName = publicName,
            Outcome = outcome,
            ErrorMessage = errorMessage,
        });
    }

    /// <summary>
    /// Best effort: the caller's token may already be cancelled, so this send uses its own short deadline.
    /// Agents older than protocol 1.1 do not know the message and are skipped.
    /// </summary>
    private async Task CancelAtAgentAsync(RegisteredTool tool, string requestId)
    {
        if (!ProtocolVersion.AtLeast(tool.AgentProtocolVersion, ProtocolVersion.Major, ProtocolVersion.CancelCallMinor))
        {
            return;
        }

        using var deadline = new CancellationTokenSource(CancelSendTimeout);
        try
        {
            await invoker.CancelCallAsync(tool.ConnectionId, requestId, deadline.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not send CancelCall for request {RequestId} to connection {ConnectionId}: {Message}", requestId, tool.ConnectionId, ex.Message);
        }
    }

    private CallToolResult Map(CallToolResponse response, string publicName)
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

        return new CallToolResult
        {
            IsError = response.IsError,
            Content = content,
            StructuredContent = ParseStructured(response.StructuredContentJson, publicName),
            Meta = ParseMeta(response.MetaJson, publicName),
        };
    }

    /// <summary>A broken optional part is dropped with a warning; the call itself still succeeds.</summary>
    private JsonElement? ParseStructured(string? json, string publicName)
    {
        // JsonElement is a struct: a default value would be an "undefined" element, not null, so parse explicitly.
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Dropped the structured content of tool {Tool}: {Message}", publicName, ex.Message);
            return null;
        }
    }

    private JsonObject? ParseMeta(string? json, string publicName)
    {
        return Optional(json, "_meta", publicName, () => JsonSerializer.Deserialize<JsonObject>(json ?? string.Empty, McpJsonUtilities.DefaultOptions));
    }

    private T? Optional<T>(string? json, string what, string publicName, Func<T?> parse)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return parse();
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Dropped the {What} of tool {Tool}: {Message}", what, publicName, ex.Message);
            return null;
        }
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
