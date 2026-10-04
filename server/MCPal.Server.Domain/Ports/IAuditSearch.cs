namespace MCPal.Server.Audit;

/// <summary>The provider-specific part of the audit log filter: text search needs the database's own case-insensitive pattern match.</summary>
internal interface IAuditSearch
{
    /// <summary>Rows whose public or original tool name contains <paramref name="text"/> (case-insensitive, <paramref name="text"/> is matched literally).</summary>
    IQueryable<ToolCallAudit> WhereToolNameContains(IQueryable<ToolCallAudit> query, string text);
}
