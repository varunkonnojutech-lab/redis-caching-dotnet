using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace RedisDemo.Core;

/// <summary>
/// A cache entry with two deadlines instead of one. This is the shape that
/// makes the two hard production problems solvable:
///
///   - <see cref="SoftExpiresAt"/>: after this, the value is stale but still
///     usable. Serve it, and refresh it in the background.
///   - <see cref="HardExpiresAt"/>: after this, the value must not be served.
///     The caller waits for a fresh load.
///
/// Without the soft deadline, a popular key expiring means hundreds of
/// concurrent requests all miss at once and all hit the database — the
/// cache stampede. With it, one request refreshes while everyone else keeps
/// getting (slightly stale) answers.
/// </summary>
public sealed record CachedEnvelope<T>(
    T Value,
    DateTimeOffset SoftExpiresAt,
    DateTimeOffset HardExpiresAt);

/// <summary>
/// Stampede-safe cache: concurrent loads of the same key collapse into one
/// database call (single-flight), and a stale-but-usable value is served
/// while exactly one background refresh runs (stale-while-revalidate).
///
/// Honest limits, stated plainly:
///   - The per-key lock is in-process. Across several app servers, two
///     servers can still load the same key at once — that's what Redis
///     distributed locks (RedLock, or SET NX with expiry) are for, and they
///     are the natural next step, not something this repo pretends to solve.
///   - A background refresh that throws is swallowed (the stale value stays
///     until hard expiry). In production, log it — never let a fire-and-forget
///     task fail silently.
/// </summary>
public sealed class StampedeSafeCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDistributedCache _cache;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public StampedeSafeCache(IDistributedCache cache) => _cache = cache;

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan softTtl,
        TimeSpan hardTtl,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = await ReadAsync<T>(key, ct);

        // Fresh: the fast path, no lock, no database.
        if (envelope is not null && now < envelope.SoftExpiresAt)
        {
            return envelope.Value;
        }

        // Stale but usable: serve it now, refresh behind the scenes.
        if (envelope is not null && now < envelope.HardExpiresAt)
        {
            _ = RefreshInBackgroundAsync(key, factory, softTtl, hardTtl);
            return envelope.Value;
        }

        // Cold, or past hard expiry: exactly one caller loads; the rest wait
        // on the gate and then read what the winner stored (double-check).
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            envelope = await ReadAsync<T>(key, ct);
            if (envelope is not null && DateTimeOffset.UtcNow < envelope.HardExpiresAt)
            {
                return envelope.Value;
            }

            var value = await factory(ct);
            await WriteAsync(key, value, softTtl, hardTtl, ct);
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RefreshInBackgroundAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan softTtl,
        TimeSpan hardTtl)
    {
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        // Non-blocking: if someone is already refreshing, there is nothing to do.
        if (!await gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            // The value may have been refreshed while we waited — re-check.
            var envelope = await ReadAsync<T>(key);
            if (envelope is not null && DateTimeOffset.UtcNow < envelope.SoftExpiresAt)
            {
                return;
            }

            var value = await factory(CancellationToken.None);
            await WriteAsync(key, value, softTtl, hardTtl);
        }
        catch
        {
            // Swallowed on purpose: the stale value stays servable until hard
            // expiry. In production, log here — a refresh that keeps failing
            // is how a cache goes stale for hours without anyone noticing.
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CachedEnvelope<T>?> ReadAsync<T>(string key, CancellationToken ct = default)
    {
        var bytes = await _cache.GetAsync(key, ct);
        return bytes is null ? null : JsonSerializer.Deserialize<CachedEnvelope<T>>(bytes, JsonOptions);
    }

    private Task WriteAsync<T>(
        string key,
        T value,
        TimeSpan softTtl,
        TimeSpan hardTtl,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = new CachedEnvelope<T>(value, now + softTtl, now + hardTtl);
        return _cache.SetAsync(
            key,
            JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = hardTtl },
            ct);
    }
}
