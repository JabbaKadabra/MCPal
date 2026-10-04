using System.Security.Cryptography;
using System.Text;
using MCPal.Server.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCPal.Server.Tenancy;

internal sealed record CreatedEnrollment(string Code, DateTimeOffset ExpiresAt);

internal sealed record RedeemedEnrollment(string ApiKey);

/// <summary>
/// Single-use enrollment codes: an owner creates one on the Setup page, a bridge trades it for its own bridge key. The code is stored
/// hashed. The company comes from the code, never from the caller, so a code can only produce a key for the company that made it.
/// </summary>
internal sealed class BridgeEnrollmentService(
    IMcpalData db,
    IApiKeyService apiKeys,
    TimeProvider timeProvider,
    IOptions<McpalOptions> options,
    ILogger<BridgeEnrollmentService> logger)
{
    public const string CodePrefix = "mcpale_";

    /// <summary>Unredeemed, unexpired codes one company may hold. A soft limit: two requests at the same instant can both pass it.</summary>
    public const int MaxOpenCodes = 10;

    private const int CodeLength = 24;
    private const int MaxCodeLength = 64;
    private const int MaxBridgeNameLength = 100;

    /// <returns>The new code, or null when the company already holds <see cref="MaxOpenCodes"/> open codes.</returns>
    public async Task<CreatedEnrollment?> CreateAsync(Guid companyId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var now = timeProvider.GetUtcNow();
        await db.Query<BridgeEnrollment>().Where(e => e.CompanyId == companyId && e.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var open = await db.Query<BridgeEnrollment>().AsNoTracking()
            .CountAsync(e => e.CompanyId == companyId && e.RedeemedAt == null && e.ExpiresAt > now, cancellationToken);
        if (open >= MaxOpenCodes)
        {
            return null;
        }

        var code = CodePrefix + RandomNumberGenerator.GetString(ApiKeyService.Alphabet, CodeLength);
        var expiresAt = now.AddMinutes(options.Value.EnrollmentLifetimeMinutes);
        db.Add(new BridgeEnrollment
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            CodeHash = ApiKeyService.Hash(code),
            CreatedByUserId = userId,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedEnrollment(code, expiresAt);
    }

    /// <returns>The raw bridge key, or null when the code is unknown, used, expired or its creator or company is no longer active. The caller cannot tell which.</returns>
    public async Task<RedeemedEnrollment?> RedeemAsync(string? code, string? bridgeName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength || !code.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            logger.LogInformation("Bridge enrollment rejected: malformed code");
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var hash = ApiKeyService.Hash(code);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);

        // The conditional update is the claim: of two requests with the same code only one changes a row (the other waits for the row lock and then no longer matches).
        var claimed = await db.Query<BridgeEnrollment>()
            .Where(e => e.CodeHash == hash && e.RedeemedAt == null && e.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.RedeemedAt, now), cancellationToken);
        if (claimed == 0)
        {
            logger.LogInformation("Bridge enrollment rejected: unknown, used or expired code");
            return null;
        }

        var enrollment = await db.Query<BridgeEnrollment>().AsNoTracking().SingleAsync(e => e.CodeHash == hash, cancellationToken);
        var creatorActive = await db.Query<PortalUser>().AsNoTracking()
            .Active(db.Query<Company>())
            .AnyAsync(u => u.Id == enrollment.CreatedByUserId && u.CompanyId == enrollment.CompanyId, cancellationToken);
        if (!creatorActive)
        {
            // Dispose without commit rolls the claim back.
            logger.LogInformation("Bridge enrollment rejected: creator or company of {EnrollmentId} is not active", enrollment.Id);
            return null;
        }

        var created = await apiKeys.CreateAsync(enrollment.CompanyId, NewApiKey.Bridge($"Bridge {CleanName(bridgeName)}", enrollment.CreatedByUserId), cancellationToken);
        await db.Query<BridgeEnrollment>().Where(e => e.Id == enrollment.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.ApiKeyId, created.Id), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Bridge enrollment {EnrollmentId} redeemed for company {CompanyId}", enrollment.Id, enrollment.CompanyId);
        return new RedeemedEnrollment(created.RawKey);
    }

    private static string CleanName(string? bridgeName)
    {
        var builder = new StringBuilder();
        foreach (var c in bridgeName ?? string.Empty)
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        var name = builder.ToString().Trim();
        if (name.Length > MaxBridgeNameLength)
        {
            name = name[..MaxBridgeNameLength].TrimEnd();
        }

        return name.Length == 0 ? "bridge" : name;
    }
}
