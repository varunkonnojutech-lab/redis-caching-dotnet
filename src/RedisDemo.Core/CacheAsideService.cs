using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace RedisDemo.Core;

/// <summary>
/// The cache-aside pattern, the one you'll use most: look in the cache first,
/// and only go to the database on a miss — then store what you found so the
/// next caller doesn't pay for the same query.
///
/// Two deliberate choices:
///   1. It depends on <see cref="IDistributedCache"/>, not on Redis. The same
///      code runs against <c>DistributedMemoryCache</c> in tests and against
///      real Redis in production — swap the registration, not the logic.
///   2. It never swallows a cache failure. If Redis is down, <c>GetAsync</c>
///      throws instead of silently hammering the database. Whether you want
///      fail-open (catch and fall through to the DB) is a product decision —
///      fail-open hides outages and can stampede the database, so the default
///      here is loud, and the caller decides.
/// </summary>
public sealed class CacheAsideService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDistributedCache _cache;

    public CacheAsideService(IDistributedCache cache) => _cache = cache;

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var bytes = await _cache.GetAsync(key, ct);
        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes, JsonOptions);
    }

    /// <summary>
    /// Returns the cached value, or loads it with <paramref name="factory"/>,
    /// stores it, and returns it. Pass <paramref name="options"/> to control
    /// expiry: absolute (dies at a fixed time, good for reference data) vs
    /// sliding (dies after inactivity, good for hot-but-bursty data).
    /// </summary>
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        DistributedCacheEntryOptions? options = null,
        CancellationToken ct = default)
    {
        var cached = await GetAsync<T>(key, ct);
        if (cached is not null)
        {
            return cached;
        }

        var value = await factory(ct);
        await SetAsync(key, value, options, ct);
        return value;
    }

    public Task SetAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions? options = null,
        CancellationToken ct = default) =>
        _cache.SetAsync(
            key,
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            options ?? new DistributedCacheEntryOptions(),
            ct);

    public Task RemoveAsync(string key, CancellationToken ct = default) =>
        _cache.RemoveAsync(key, ct);
}
