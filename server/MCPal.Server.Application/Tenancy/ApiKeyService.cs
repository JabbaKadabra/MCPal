using System.Security.Cryptography;
using System.Text;
using MCPal.Server.Ports;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Server.Tenancy;

internal sealed record CreatedApiKey(
    Guid Id,
    string Name,
    string Prefix,
    string RawKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    ApiKeyPurpose Purpose,
    string? UserId);

/// <param name="UserId">The user a personal key acts as; null for bridge keys.</param>
internal sealed record ValidatedKey(Guid CompanyId, Guid ApiKeyId, ApiKeyPurpose Purpose, string? UserId);

/// <summary>Notified after an API key is revoked, e.g. to close its tunnels.</summary>
internal interface IApiKeyRevocationListener
{
    Task OnRevokedAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken);
}

/// <param name="UserId">Required for personal keys (the user the key acts as), must be null for bridge keys.</param>
/// <param name="CreatedByUserId">The portal user who creates the key, so owners can see who made a bridge key.</param>
internal sealed record NewApiKey(string Name, DateTimeOffset? ExpiresAt, ApiKeyPurpose Purpose, string? UserId, string? CreatedByUserId)
{
    /// <summary>A personal access token of <paramref name="userId"/>, created by that user.</summary>
    public static NewApiKey Personal(string name, string userId, DateTimeOffset? expiresAt = null) =>
        new(name, expiresAt, ApiKeyPurpose.Personal, userId, userId);

    /// <summary>A company key for bridges.</summary>
    public static NewApiKey Bridge(string name, string? createdByUserId = null, DateTimeOffset? expiresAt = null) =>
        new(name, expiresAt, ApiKeyPurpose.Bridge, null, createdByUserId);
}

internal interface IApiKeyService
{
    Task<CreatedApiKey> CreateAsync(Guid companyId, NewApiKey request, CancellationToken cancellationToken);

    Task<ValidatedKey?> ValidateAsync(string rawKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApiKey>> ListAsync(Guid companyId, CancellationToken cancellationToken);

    /// <summary>Revokes a key of the given company. Returns false when the company owns no such key.</summary>
    Task<bool> RevokeAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken);

    /// <summary>Whether the key exists, is enabled, unexpired, its company is enabled and (personal keys) its user is active.</summary>
    Task<bool> IsActiveAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken);

    /// <summary>The subset of the given keys that is active, with the company each belongs to.</summary>
    Task<IReadOnlyList<ValidatedKey>> ActiveKeysAsync(IReadOnlyCollection<Guid> apiKeyIds, CancellationToken cancellationToken);
}

internal sealed class ApiKeyService(
    IMcpalData db,
    TimeProvider timeProvider,
    IEnumerable<IApiKeyRevocationListener> revocationListeners) : IApiKeyService
{
    public const string KeyPrefix = "mcpal_";

    private const int SecretLength = 40;

    /// <summary>"mcpal_" + 8 company characters + "_" + 6 secret characters, so keys of one company can be told apart.</summary>
    private const int DisplayPrefixLength = 21;
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(5);

    public async Task<CreatedApiKey> CreateAsync(Guid companyId, NewApiKey request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        var (name, expiresAt, purpose, userId, createdByUserId) = request;
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentException("Unknown key purpose.", nameof(request));
        }

        if (purpose == ApiKeyPurpose.Personal && string.IsNullOrEmpty(userId))
        {
            throw new ArgumentException("A personal access token needs a user.", nameof(request));
        }

        if (purpose == ApiKeyPurpose.Bridge && userId is not null)
        {
            throw new ArgumentException("Bridge keys belong to the company, not to a user.", nameof(request));
        }

        var rawKey = $"{KeyPrefix}{companyId.ToString("N")[..8]}_{RandomNumberGenerator.GetString(Alphabet, SecretLength)}";
        var now = timeProvider.GetUtcNow();
        var entity = new ApiKey
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Name = name.Trim(),
            Prefix = rawKey[..DisplayPrefixLength],
            KeyHash = Hash(rawKey),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            Purpose = purpose,
            UserId = userId,
            CreatedByUserId = createdByUserId,
        };
        db.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedApiKey(entity.Id, entity.Name, entity.Prefix, rawKey, entity.CreatedAt, entity.ExpiresAt, entity.Purpose, entity.UserId);
    }

    public async Task<ValidatedKey?> ValidateAsync(string rawKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(rawKey) || !rawKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(rawKey);
        var now = timeProvider.GetUtcNow();
        var key = await db.Query<ApiKey>().AsNoTracking()
            .Active(db.Query<Company>(), db.Query<PortalUser>(), now)
            .Where(k => k.KeyHash == hash)
            .Select(k => new { k.Id, k.CompanyId, k.LastUsedAt, k.Purpose, k.UserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (key is null)
        {
            return null;
        }

        if (key.LastUsedAt is null || now - key.LastUsedAt > LastUsedThrottle)
        {
            await db.Query<ApiKey>().Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }

        return new ValidatedKey(key.CompanyId, key.Id, key.Purpose, key.UserId);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        return await db.Query<ApiKey>().AsNoTracking()
            .Where(k => k.CompanyId == companyId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RevokeAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken)
    {
        var updated = await db.Query<ApiKey>()
            .Where(k => k.Id == apiKeyId && k.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.Disabled, true), cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        foreach (var listener in revocationListeners)
        {
            await listener.OnRevokedAsync(companyId, apiKeyId, cancellationToken);
        }

        return true;
    }

    public async Task<bool> IsActiveAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken)
    {
        return await db.Query<ApiKey>().AsNoTracking()
            .Active(db.Query<Company>(), db.Query<PortalUser>(), timeProvider.GetUtcNow())
            .AnyAsync(k => k.Id == apiKeyId && k.CompanyId == companyId, cancellationToken);
    }

    public async Task<IReadOnlyList<ValidatedKey>> ActiveKeysAsync(IReadOnlyCollection<Guid> apiKeyIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKeyIds);

        return await db.Query<ApiKey>().AsNoTracking()
            .Active(db.Query<Company>(), db.Query<PortalUser>(), timeProvider.GetUtcNow())
            .Where(k => apiKeyIds.Contains(k.Id))
            .Select(k => new ValidatedKey(k.CompanyId, k.Id, k.Purpose, k.UserId))
            .ToListAsync(cancellationToken);
    }

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
}
