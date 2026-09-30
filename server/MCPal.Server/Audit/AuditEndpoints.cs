using System.Globalization;
using System.Security.Claims;
using System.Text;
using MCPal.Server.Diagnostics;
using MCPal.Server.Portal;
using MCPal.Server.Storage;
using MCPal.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.Audit;

internal sealed record AuditEntryResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    int DurationMs,
    string AuthKind,
    string? UserId,
    string? UserEmail,
    Guid? ApiKeyId,
    string? ApiKeyName,
    [property: System.Text.Json.Serialization.JsonPropertyName("oauthClientId")] string? OAuthClientId,
    string BridgeName,
    string ServerName,
    string ToolName,
    string PublicName,
    string Outcome,
    string? ErrorMessage);

internal sealed record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Items, string? NextCursor);

/// <summary>
/// The audit log of the signed-in owner's company (it names people, so members do not see it). The company always comes from the
/// user, never from the request.
/// </summary>
internal static class AuditEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxExportRows = 100_000;

    private static readonly string[] CsvColumns =
    [
        "occurredAt", "durationMs", "authKind", "userId", "userEmail", "apiKeyId", "apiKeyName", "oauthClientId",
        "bridgeName", "serverName", "toolName", "publicName", "outcome", "errorMessage",
    ];

    public static void Map(RouteGroupBuilder secured)
    {
        ArgumentNullException.ThrowIfNull(secured);

        var owners = secured.MapGroup(string.Empty).AddEndpointFilter<OwnerOnlyFilter>();
        owners.MapGet("audit", ListAsync);
        owners.MapGet("audit/export.csv", ExportAsync);
    }

    private static async Task<IResult> ListAsync(HttpRequest request, ClaimsPrincipal principal, UserManager<PortalUser> users, MCPalDbContext db, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        if (!AuditFilter.TryParse(request.Query, out var filter, out var errors))
        {
            return PortalEndpoints.Problems(errors);
        }

        var rows = await filter.Apply(db.ToolCallAudits.AsNoTracking().Where(a => a.CompanyId == companyId))
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Take(filter.Limit + 1)
            .ToListAsync(cancellationToken);
        var page = rows.Take(filter.Limit).ToList();
        var names = await NamesAsync(db, companyId, cancellationToken);
        var next = rows.Count > filter.Limit ? AuditFilter.EncodeCursor(page[^1]) : null;
        return Results.Json(new AuditPageResponse([.. page.Select(a => ToResponse(a, names))], next));
    }

    private static async Task<IResult> ExportAsync(HttpRequest request, ClaimsPrincipal principal, UserManager<PortalUser> users, MCPalDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await PortalEndpoints.CompanyOfAsync(principal, users) is not { } companyId)
        {
            return Results.Unauthorized();
        }

        if (!AuditFilter.TryParse(request.Query, out var filter, out var errors))
        {
            return PortalEndpoints.Problems(errors);
        }

        var names = await NamesAsync(db, companyId, cancellationToken);
        var query = filter.Apply(db.ToolCallAudits.AsNoTracking().Where(a => a.CompanyId == companyId))
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Take(MaxExportRows);
        var fileName = $"mcpal-audit-{time.GetUtcNow():yyyyMMdd-HHmmss}.csv";
        return Results.Stream(async stream =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n" };
            await writer.WriteLineAsync(string.Join(',', CsvColumns));
            await foreach (var audit in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var entry = ToResponse(audit, names);
                await writer.WriteLineAsync(string.Join(',', new[]
                {
                    Cell(entry.OccurredAt.ToString("O", CultureInfo.InvariantCulture)),
                    entry.DurationMs.ToString(CultureInfo.InvariantCulture),
                    Cell(entry.AuthKind),
                    Cell(entry.UserId),
                    Cell(entry.UserEmail),
                    Cell(entry.ApiKeyId?.ToString()),
                    Cell(entry.ApiKeyName),
                    Cell(entry.OAuthClientId),
                    Cell(entry.BridgeName),
                    Cell(entry.ServerName),
                    Cell(entry.ToolName),
                    Cell(entry.PublicName),
                    Cell(entry.Outcome),
                    Cell(entry.ErrorMessage),
                }));
            }
        }, "text/csv", fileName);
    }

    /// <summary>Names of the keys and users of the company, to show instead of ids. A removed user or key has no name left.</summary>
    private sealed record Names(Dictionary<Guid, string> Keys, Dictionary<string, string> UserEmails);

    private static async Task<Names> NamesAsync(MCPalDbContext db, Guid companyId, CancellationToken cancellationToken) => new(
        await db.ApiKeys.AsNoTracking().Where(k => k.CompanyId == companyId).ToDictionaryAsync(k => k.Id, k => k.Name, cancellationToken),
        await db.Users.AsNoTracking().Where(u => u.CompanyId == companyId).ToDictionaryAsync(u => u.Id, u => u.Email ?? string.Empty, cancellationToken));

    private static AuditEntryResponse ToResponse(ToolCallAudit a, Names names) => new(
        a.Id,
        a.OccurredAt,
        a.DurationMs,
        a.AuthKind,
        a.UserId,
        a.UserId is { } userId ? names.UserEmails.GetValueOrDefault(userId) : null,
        a.ApiKeyId,
        a.ApiKeyId is { } keyId ? names.Keys.GetValueOrDefault(keyId) : null,
        a.OAuthClientId,
        a.BridgeName,
        a.ServerName,
        a.ToolName,
        a.PublicName,
        a.Outcome,
        a.ErrorMessage);

    /// <summary>
    /// One CSV cell. Texts starting with a formula character get a leading apostrophe, so a spreadsheet does not run
    /// a tool name or error message a bridge controls as a formula.
    /// </summary>
    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}

/// <summary>Query string filters of the audit endpoints.</summary>
internal sealed class AuditFilter
{
    /// <summary>Ticks of <c>DateTime.MaxValue</c>; a cursor beyond it is not a date.</summary>
    private const long MaxTicks = 3_155_378_975_999_999_999;

    private DateTimeOffset? from;
    private DateTimeOffset? to;
    private string? tool;
    private Guid? keyId;
    private string? userId;
    private string? outcome;
    private (DateTimeOffset OccurredAt, Guid Id)? cursor;

    public int Limit { get; private set; } = AuditEndpoints.DefaultLimit;

    public static bool TryParse(IQueryCollection query, out AuditFilter filter, out List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(query);

        filter = new AuditFilter();
        errors = [];
        filter.from = Date(query, "from", errors);
        filter.to = Date(query, "to", errors);
        filter.tool = query["tool"].ToString() is { Length: > 0 } tool ? tool : null;
        if (query["keyId"].ToString() is { Length: > 0 } keyText)
        {
            if (Guid.TryParse(keyText, out var keyId))
            {
                filter.keyId = keyId;
            }
            else
            {
                errors.Add("'keyId' must be a GUID.");
            }
        }

        if (query["userId"].ToString() is { Length: > 0 } userText)
        {
            if (userText.Length <= AuditColumns.UserId)
            {
                filter.userId = userText;
            }
            else
            {
                errors.Add("'userId' is not valid.");
            }
        }

        if (query["outcome"].ToString() is { Length: > 0 } outcome)
        {
            if (ToolCallOutcome.All.Contains(outcome))
            {
                filter.outcome = outcome;
            }
            else
            {
                errors.Add($"'outcome' must be one of: {string.Join(", ", ToolCallOutcome.All)}.");
            }
        }

        if (query["limit"].ToString() is { Length: > 0 } limitText)
        {
            if (int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit > 0)
            {
                filter.Limit = Math.Min(limit, AuditEndpoints.MaxLimit);
            }
            else
            {
                errors.Add("'limit' must be a positive number.");
            }
        }

        if (query["cursor"].ToString() is { Length: > 0 } cursorText)
        {
            if (TryDecodeCursor(cursorText, out var cursor))
            {
                filter.cursor = cursor;
            }
            else
            {
                errors.Add("'cursor' is not valid.");
            }
        }

        return errors.Count == 0;
    }

    /// <summary>The cursor is the sort key of the last row of a page. It carries no company, so it cannot widen the scope of a query.</summary>
    public static string EncodeCursor(ToolCallAudit last) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{last.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{last.Id:N}"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public IQueryable<ToolCallAudit> Apply(IQueryable<ToolCallAudit> query)
    {
        if (from is { } start)
        {
            query = query.Where(a => a.OccurredAt >= start);
        }

        if (to is { } end)
        {
            query = query.Where(a => a.OccurredAt <= end);
        }

        if (tool is { } text)
        {
            var pattern = "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(a => EF.Functions.ILike(a.PublicName, pattern, "\\") || EF.Functions.ILike(a.ToolName, pattern, "\\"));
        }

        if (keyId is { } key)
        {
            query = query.Where(a => a.ApiKeyId == key);
        }

        if (userId is { } user)
        {
            query = query.Where(a => a.UserId == user);
        }

        if (outcome is { } result)
        {
            query = query.Where(a => a.Outcome == result);
        }

        if (cursor is { } position)
        {
            var (occurredAt, id) = position;
            query = query.Where(a => a.OccurredAt < occurredAt || (a.OccurredAt == occurredAt && a.Id.CompareTo(id) < 0));
        }

        return query;
    }

    private static DateTimeOffset? Date(IQueryCollection query, string name, List<string> errors)
    {
        if (query[name].ToString() is not { Length: > 0 } text)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            return value;
        }

        errors.Add($"'{name}' must be a date and time such as 2026-09-30T12:00:00Z.");
        return null;
    }

    private static bool TryDecodeCursor(string text, out (DateTimeOffset OccurredAt, Guid Id) cursor)
    {
        cursor = default;
        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParse(parts[1], out var id)
                && ticks is >= 0 and <= MaxTicks)
            {
                cursor = (new DateTimeOffset(ticks, TimeSpan.Zero), id);
                return true;
            }
        }
        catch (FormatException)
        {
            // Not base64: invalid cursor.
        }

        return false;
    }
}
