using System.Diagnostics;
using MCPal.Contracts;

namespace MCPal.Agent.Local;

/// <summary>Spans of the agent. The source is created here, not held in static state; the host listens to it by name.</summary>
internal sealed class AgentTelemetry : IDisposable
{
    public const string ActivitySourceName = "MCPal.Agent";

    private readonly ActivitySource activitySource = new(ActivitySourceName);

    /// <summary>Starts the span of one local call. It joins the cloud's trace when the request carries a valid <c>traceparent</c>.</summary>
    public Activity? StartLocalCall(CallToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parent = ActivityContext.TryParse(request.TraceParent, null, out var context) ? context : default;
        var activity = activitySource.StartActivity("mcpal.local_call", ActivityKind.Internal, parent);
        activity?.SetTag("mcpal.server", request.ServerName);
        activity?.SetTag("mcpal.tool", request.ToolName);
        return activity;
    }

    public void Dispose() => activitySource.Dispose();
}
