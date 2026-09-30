namespace MCPal.Cloud.Audit;

/// <summary>
/// One relayed tool call. Arguments and results are deliberately not stored: they may hold personal or confidential data.
/// </summary>
internal sealed class ToolCallAudit
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    /// <summary>Start of the call.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public int DurationMs { get; set; }

    /// <summary><c>apikey</c> or <c>oauth</c>.</summary>
    public string AuthKind { get; set; } = string.Empty;

    public Guid? ApiKeyId { get; set; }

    public string? OAuthClientId { get; set; }

    public string AgentName { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    /// <summary>The tool name on the local server.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>The name Claude called, <c>server__tool</c>.</summary>
    public string PublicName { get; set; } = string.Empty;

    /// <summary>One of <c>ToolCallOutcome</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>A short message without stack trace.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>Column sizes of <see cref="ToolCallAudit"/>; the writer truncates texts to them.</summary>
internal static class AuditColumns
{
    public const int AuthKind = 16;
    public const int OAuthClientId = 64;
    public const int Name = 200;
    public const int PublicName = 64;
    public const int Outcome = 16;
    public const int ErrorMessage = 500;
}
