using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCPal.Server.Ports;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MCPal.Server.Access.UserContext;

/// <summary>A signing key with its private key loaded, ready to sign, plus the facts the JWKS needs.</summary>
internal sealed record LoadedSigningKey(
    string Kid,
    ECDsaSecurityKey SecurityKey,
    string PublicJwkJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset ActivatesAt,
    DateTimeOffset RetiresAt,
    DateTimeOffset RemoveAt);

/// <summary>An immutable snapshot of the keys, replaced as a whole.</summary>
internal sealed class KeySet(IReadOnlyList<LoadedSigningKey> keys, DateTimeOffset loadedAt)
{
    public IReadOnlyList<LoadedSigningKey> Keys { get; } = keys;

    public DateTimeOffset LoadedAt { get; } = loadedAt;

    /// <summary>The key that signs now: the most recently activated one of those whose time has come and not yet gone.</summary>
    public LoadedSigningKey? SigningKey(DateTimeOffset now) =>
        Keys.Where(k => k.ActivatesAt <= now && now < k.RetiresAt).MaxBy(k => k.ActivatesAt);

    /// <summary>The keys a verifier may need: the signing key, its pre-published successor and retired keys still in their grace period.</summary>
    public IReadOnlyList<LoadedSigningKey> Published(DateTimeOffset now) => [.. Keys.Where(k => now < k.RemoveAt).OrderBy(k => k.ActivatesAt)];
}

/// <summary>
/// The signing keys of the MCPal server: stored in the database (private part protected by Data Protection), held in memory as an
/// immutable <see cref="KeySet"/> that is swapped with <see cref="Interlocked.Exchange{T}(ref T, T)"/>. <see cref="RotateAsync"/> creates
/// the first key, pre-publishes the successor before the current key retires and removes keys after their grace period;
/// the rotation service calls it hourly, and a caller that finds no usable key calls it too.
/// </summary>
internal sealed class SigningKeyStore(
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    IOptions<McpalOptions> options,
    TimeProvider timeProvider,
    ILogger<SigningKeyStore> logger)
{
    public const string ProtectorPurpose = "MCPal.UserContext.SigningKey.v1";

    /// <summary>The successor is created (and listed in the JWKS) this long before the current key retires.</summary>
    public static readonly TimeSpan PrePublishWindow = TimeSpan.FromDays(2);

    /// <summary>A retired key stays in the JWKS this long, so tokens signed just before it retired still verify.</summary>
    public static readonly TimeSpan RetirementGrace = TimeSpan.FromDays(7);

    /// <summary>The in-memory keys are read from the database again after this long, so other instances' rotations are picked up.</summary>
    public static readonly TimeSpan ReloadInterval = TimeSpan.FromMinutes(5);

    private const long AdvisoryLockKey = 7_316_442_001;

    private readonly IDataProtector protector = dataProtection.CreateProtector(ProtectorPurpose);
    private KeySet? current;
    private Task? refresh;

    /// <summary>The key to sign with now.</summary>
    public async Task<LoadedSigningKey> GetSigningKeyAsync(CancellationToken cancellationToken)
    {
        var keys = await FreshAsync(cancellationToken);
        return keys.SigningKey(timeProvider.GetUtcNow()) ?? throw new InvalidOperationException("No signing key is available.");
    }

    /// <summary>The keys that belong in the JWKS now.</summary>
    public async Task<IReadOnlyList<LoadedSigningKey>> GetPublishedKeysAsync(CancellationToken cancellationToken) =>
        (await FreshAsync(cancellationToken)).Published(timeProvider.GetUtcNow());

    /// <summary>Creates, pre-publishes and removes keys as the time requires, then reloads the key set. Safe to call from several instances.</summary>
    public async Task RotateAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IMcpalData>();
        await using (var transaction = await db.BeginTransactionAsync(cancellationToken))
        {
            // One instance at a time decides what to create, so two instances never start the same rotation twice.
            await transaction.LockAsync(AdvisoryLockKey, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var keys = await db.Query<SigningKey>().ToListAsync(cancellationToken);

            var expired = keys.Where(k => k.RemoveAt <= now).ToList();
            db.RemoveRange(expired);

            // A key whose private part cannot be unprotected (lost or replaced key ring) cannot sign, so it does not count as the current key.
            keys = [.. keys.Except(expired).Where(IsReadable)];

            if (!keys.Any(k => k.ActivatesAt <= now && now < k.RetiresAt))
            {
                db.Add(CreateKey(now, now));
            }
            else if (keys.MaxBy(k => k.RetiresAt) is { } newest && newest.RetiresAt - now <= PrePublishWindow)
            {
                db.Add(CreateKey(now, newest.RetiresAt));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        Interlocked.Exchange(ref current, await LoadAsync(db, cancellationToken));
    }

    private async Task<KeySet> FreshAsync(CancellationToken cancellationToken)
    {
        var keys = Volatile.Read(ref current);
        var now = timeProvider.GetUtcNow();
        if (keys is not null && now - keys.LoadedAt < ReloadInterval && keys.SigningKey(now) is not null)
        {
            return keys;
        }

        // Callers that arrive while one refresh runs wait for it instead of starting their own.
        var running = Volatile.Read(ref refresh);
        if (running is null || running.IsCompleted)
        {
            var started = RotateAsync(CancellationToken.None);
            running = Interlocked.CompareExchange(ref refresh, started, running) == running ? started : Volatile.Read(ref refresh) ?? started;
        }

        await running.WaitAsync(cancellationToken);
        return Volatile.Read(ref current) ?? throw new InvalidOperationException("The signing keys could not be loaded.");
    }

    private bool IsReadable(SigningKey key)
    {
        try
        {
            _ = protector.Unprotect(Convert.FromBase64String(key.ProtectedPrivateKey));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private async Task<KeySet> LoadAsync(IMcpalData db, CancellationToken cancellationToken)
    {
        var rows = await db.Query<SigningKey>().AsNoTracking().ToListAsync(cancellationToken);
        var loaded = new List<LoadedSigningKey>();
        foreach (var row in rows)
        {
            try
            {
                var ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(protector.Unprotect(Convert.FromBase64String(row.ProtectedPrivateKey)), out _);
                loaded.Add(new LoadedSigningKey(row.Kid, new ECDsaSecurityKey(ecdsa) { KeyId = row.Kid }, row.PublicJwkJson, row.CreatedAt, row.ActivatesAt, row.RetiresAt, row.RemoveAt));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // A lost or replaced Data Protection key ring makes the key unreadable. It is skipped; a new key is created when none else signs.
                logger.LogError(ex, "Signing key {Kid} cannot be read (is the Data Protection key ring intact?) and is ignored", row.Kid);
            }
        }

        return new KeySet(loaded, timeProvider.GetUtcNow());
    }

    private SigningKey CreateKey(DateTimeOffset now, DateTimeOffset activatesAt)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        var x = WebEncoders.Base64UrlEncode(parameters.Q.X ?? []);
        var y = WebEncoders.Base64UrlEncode(parameters.Q.Y ?? []);

        // RFC 7638: the thumbprint hashes the required members in lexicographic order.
        var kid = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes($$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""")));
        var jwk = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = x,
            ["y"] = y,
            ["kid"] = kid,
            ["use"] = "sig",
            ["alg"] = SigningKey.EllipticCurveAlgorithm,
        });
        var lifetime = TimeSpan.FromDays(options.Value.UserContext.SigningKeyLifetimeDays);
        return new SigningKey
        {
            Kid = kid,
            Algorithm = SigningKey.EllipticCurveAlgorithm,
            PublicJwkJson = jwk,
            ProtectedPrivateKey = Convert.ToBase64String(protector.Protect(ecdsa.ExportPkcs8PrivateKey())),
            CreatedAt = now,
            ActivatesAt = activatesAt,
            RetiresAt = activatesAt + lifetime,
            RemoveAt = activatesAt + lifetime + RetirementGrace,
        };
    }
}
