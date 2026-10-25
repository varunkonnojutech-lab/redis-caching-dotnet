namespace RedisDemo.Core;

/// <summary>
/// Cache invalidation across app instances, via pub/sub.
///
/// The problem it solves: with two servers each holding their own cache (or
/// sharing Redis but caching locally too), server A updating a product has
/// no way to tell server B its copy is stale — unless it publishes an
/// invalidation message and every server subscribes. The message carries
/// only the cache key, never the data: subscribers re-read from the source
/// of truth on their next request, so a missed message costs one stale read,
/// not a permanently wrong value.
///
/// Two implementations ship in this repo: <see cref="InMemoryInvalidationChannel"/>
/// (tests and local demos) and <see cref="RedisInvalidationChannel"/> (real Redis).
/// Same interface, so the application code never changes.
/// </summary>
public interface ICacheInvalidationChannel
{
    /// <summary>Announce that <paramref name="cacheKey"/> is stale.</summary>
    Task PublishInvalidationAsync(string cacheKey, CancellationToken ct = default);

    /// <summary>
    /// Subscribe <paramref name="onInvalidated"/> to invalidation messages.
    /// A typical handler removes the key from its local cache.
    /// </summary>
    Task SubscribeAsync(Func<string, Task> onInvalidated, CancellationToken ct = default);
}
