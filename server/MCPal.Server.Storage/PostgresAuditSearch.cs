using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.Audit;

/// <summary><see cref="IAuditSearch"/> with PostgreSQL <c>ILIKE</c>; the wildcard characters of the text are escaped.</summary>
internal sealed class PostgresAuditSearch : IAuditSearch
{
    public IQueryable<ToolCallAudit> WhereToolNameContains(IQueryable<ToolCallAudit> query, string text)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(text);

        var pattern = "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return query.Where(a => EF.Functions.ILike(a.PublicName, pattern, "\\") || EF.Functions.ILike(a.ToolName, pattern, "\\"));
    }
}
