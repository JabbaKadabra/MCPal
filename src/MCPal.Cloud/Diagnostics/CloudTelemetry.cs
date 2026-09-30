using System.Diagnostics;
using System.Diagnostics.Metrics;
using MCPal.Cloud.Tunnel;
using MCPal.Contracts;

namespace MCPal.Cloud.Diagnostics;

/// <summary>How a relayed tool call ended. Shared by metrics, spans and the audit log.</summary>
internal static class ToolCallOutcome
{
    public const string Ok = "ok";
    public const string ToolError = "tool_error";
    public const string Timeout = "timeout";
    public const string Offline = "offline";
    public const string RelayError = "relay_error";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Ok, ToolError, Timeout, Offline, RelayError, Cancelled];

    public static string From(CallToolResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.IsError ? ToolError : Ok;
    }
}

/// <summary>
/// Custom spans and metrics of the cloud. Meters come from the injected <see cref="IMeterFactory"/>, so there is no static state.
/// The company id goes on spans only: as a metric tag it would raise cardinality and expose customers in shared dashboards.
/// </summary>
internal sealed class CloudTelemetry : IDisposable
{
    public const string MeterName = "MCPal.Cloud";
    public const string ActivitySourceName = "MCPal.Cloud";

    private readonly ActivitySource activitySource = new(ActivitySourceName);
    private readonly Meter meter;
    private readonly Counter<long> toolCalls;
    private readonly Histogram<double> toolCallDuration;
    private readonly Counter<long> registrations;
    private readonly Counter<long> rateLimitRejections;
    private readonly Counter<long> auditDropped;

    public CloudTelemetry(IMeterFactory meterFactory, ConnectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(registry);

        meter = meterFactory.Create(MeterName);
        toolCalls = meter.CreateCounter<long>("mcpal.tool_calls", description: "Relayed tool calls by outcome.");
        toolCallDuration = meter.CreateHistogram<double>("mcpal.tool_call.duration", unit: "s", description: "Duration of relayed tool calls by outcome.");
        registrations = meter.CreateCounter<long>("mcpal.agent.registrations", description: "Agent Register calls by result (accepted, partial, rejected).");
        rateLimitRejections = meter.CreateCounter<long>("mcpal.rate_limit.rejections", description: "Requests rejected by a rate limit policy.");
        auditDropped = meter.CreateCounter<long>("mcpal.audit.dropped", description: "Audit entries lost because the queue was full or the database write failed.");
        meter.CreateObservableGauge("mcpal.tunnels.active", () => registry.AllConnections().Count, description: "Live agent tunnels.");
    }

    public Activity? StartToolCall(Guid companyId, string serverName, string toolName)
    {
        var activity = activitySource.StartActivity("mcpal.tool_call");
        activity?.SetTag("mcpal.company_id", companyId.ToString());
        activity?.SetTag("mcpal.server", serverName);
        activity?.SetTag("mcpal.tool", toolName);
        return activity;
    }

    public void RecordToolCall(Activity? activity, string outcome, TimeSpan duration)
    {
        activity?.SetTag("mcpal.outcome", outcome);
        if (outcome is ToolCallOutcome.ToolError or ToolCallOutcome.Timeout or ToolCallOutcome.RelayError)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        var tag = new KeyValuePair<string, object?>("outcome", outcome);
        toolCalls.Add(1, tag);
        toolCallDuration.Record(duration.TotalSeconds, tag);
    }

    public void RecordRegistration(RegisterResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var kind = !result.Accepted ? "rejected" : result.RejectedServers.Count > 0 || result.RejectedTools.Count > 0 ? "partial" : "accepted";
        registrations.Add(1, new KeyValuePair<string, object?>("result", kind));
    }

    public void RecordRateLimitRejection(string policy)
    {
        rateLimitRejections.Add(1, new KeyValuePair<string, object?>("policy", policy));
    }

    public void RecordAuditDropped(int count)
    {
        auditDropped.Add(count);
    }

    public void Dispose()
    {
        activitySource.Dispose();
        meter.Dispose();
    }
}
