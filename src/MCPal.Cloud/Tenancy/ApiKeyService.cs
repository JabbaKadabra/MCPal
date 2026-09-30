using System.Security.Cryptography;
using System.Text;
using MCPal.Cloud.Storage;
using Microsoft.EntityFrameworkCore;

namespace MCPal.Cloud.Tenancy;

internal sealed record CreatedApiKey(Guid Id, string Name, string Prefix, string RawKey, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

internal sealed record ValidatedKey(Guid CompanyId, Guid ApiKeyId);

/// <summary>Notified after an API key is revoked, e.g. to close its tunnels.</summary>
internal interface IApiKeyRevocationListener
{
    Task OnRevokedAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken);
}

internal interface IApiKeyService
{
    Task<CreatedApiKey> CreateAsync(Guid companyId, string name, DateTimeOffset? expiresAt, CancellationToken cancellationToken);

    Task<ValidatedKey?> ValidateAsync(string rawKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApiKey>> ListAsync(Guid companyId, CancellationToken cancellationToken);

    /// <summary>Revokes a key of the given company. Returns false when the company owns no such key.</summary>
    Task<bool> RevokeAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken);

    /// <summary>Whether the key exists, is enabled, unexpired and its company is enabled.</summary>
    Task<bool> IsActiveAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken);

    /// <summary>The subset of the given keys that is active, with the company each belongs to.</summary>
    Task<IReadOnlyList<ValidatedKey>> ActiveKeysAsync(IReadOnlyCollection<Guid> apiKeyIds, CancellationToken cancellationToken);
}

internal sealed class ApiKeyService(
    MCPalDbContext db,
    TimeProvider timeProvider,
    IEnumerable<IApiKeyRevocationListener> revocationListeners) : IApiKeyService
{
    public const string KeyPrefix = "mcpal_";

    private const int SecretLength = 40;

    /// <summary>"mcpal_" + 8 company characters + "_" + 6 secret characters, so keys of one company can be told apart.</summary>
    private const int DisplayPrefixLength = 21;
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(5);

    public async Task<CreatedApiKey> CreateAsync(Guid companyId, string name, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

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
        };
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedApiKey(entity.Id, entity.Name, entity.Prefix, rawKey, entity.CreatedAt, entity.ExpiresAt);
    }

    public async Task<ValidatedKey?> ValidateAsync(string rawKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(rawKey) || !rawKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(rawKey);
        var now = timeProvider.GetUtcNow();
        var key = await db.ApiKeys.AsNoTracking()
            .Active(db.Companies, now)
            .Where(k => k.KeyHash == hash)
            .Select(k => new { k.Id, k.CompanyId, k.LastUsedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (key is null)
        {
            return null;
        }

        if (key.LastUsedAt is null || now - key.LastUsedAt > LastUsedThrottle)
        {
            await db.ApiKeys.Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }

        return new ValidatedKey(key.CompanyId, key.Id);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        return await db.ApiKeys.AsNoTracking()
            .Where(k => k.CompanyId == companyId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RevokeAsync(Guid companyId, Guid apiKeyId, CancellationToken cancellationToken)
    {
        var updated = await db.ApiKeys
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
        return await db.ApiKeys.AsNoTracking()
            .Active(db.Companies, timeProvider.GetUtcNow())
            .AnyAsync(k => k.Id == apiKeyId && k.CompanyId == companyId, cancellationToken);
    }

    public async Task<IReadOnlyList<ValidatedKey>> ActiveKeysAsync(IReadOnlyCollection<Guid> apiKeyIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKeyIds);

        return await db.ApiKeys.AsNoTracking()
            .Active(db.Companies, timeProvider.GetUtcNow())
            .Where(k => apiKeyIds.Contains(k.Id))
            .Select(k => new ValidatedKey(k.CompanyId, k.Id))
            .ToListAsync(cancellationToken);
    }

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
}
