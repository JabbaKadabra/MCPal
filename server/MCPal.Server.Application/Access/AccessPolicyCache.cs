using System.Collections.Concurrent;

namespace MCPal.Server.Access;

/// <summary>
/// Keeps the <see cref="CompanyPolicy"/> of each company for a few minutes so a tool call needs no database round trip.
/// Lock-free: every company has a generation counter that <see cref="Invalidate"/> raises. A load stores its result only if
/// the generation did not change while it ran, and an entry is used only if its generation is still current, so a policy
/// that was read before an invalidation is never served afterwards. The cache lives in this process (like the tunnel
/// registry); several server instances would need a shared invalidation signal.
/// </summary>
internal sealed class AccessPolicyCache(TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private sealed record Entry(long Generation, CompanyPolicy Policy, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<Guid, long> generations = new();
    private readonly ConcurrentDictionary<Guid, Entry> entries = new();

    public async Task<CompanyPolicy> GetAsync(Guid companyId, Func<CancellationToken, Task<CompanyPolicy>> load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(load);

        var generation = generations.GetValueOrDefault(companyId);
        if (entries.TryGetValue(companyId, out var cached) && cached.Generation == generation && cached.ExpiresAt > timeProvider.GetUtcNow())
        {
            return cached.Policy;
        }

        var policy = await load(cancellationToken);
        if (generations.GetValueOrDefault(companyId) == generation)
        {
            entries[companyId] = new Entry(generation, policy, timeProvider.GetUtcNow().Add(Lifetime));
        }

        return policy;
    }

    /// <summary>Forgets the policy of the company. Call it after every change that can alter what a user may use.</summary>
    public void Invalidate(Guid companyId)
    {
        generations.AddOrUpdate(companyId, 1, (_, current) => current + 1);
        entries.TryRemove(companyId, out _);
    }
}
