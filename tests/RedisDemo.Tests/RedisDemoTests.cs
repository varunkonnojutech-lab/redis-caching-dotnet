using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RedisDemo.Core;

namespace RedisDemo.Tests;

/// <summary>
/// Every test runs against DistributedMemoryCache — no Redis required.
/// The code under test only depends on IDistributedCache, so these tests
/// prove the patterns, and the same code runs unchanged against real Redis.
/// </summary>
public sealed class RedisDemoTests
{
    private static MemoryDistributedCache NewCache() =>
        new(Options.Create(new MemoryDistributedCacheOptions()));

    [Fact]
    public async Task CacheAside_SecondFetch_DoesNotHitDatabase()
    {
        var cache = NewCache();
        var service = new CacheAsideService(cache);
        var dbCalls = 0;

        async Task<Product> Factory(CancellationToken ct)
        {
            dbCalls++;
            await Task.Delay(10, ct);
            return new Product(1, "Keyboard", "Hardware", 129.99m);
        }

        var first = await service.GetOrCreateAsync(CacheKey.Product(1), Factory);
        var second = await service.GetOrCreateAsync(CacheKey.Product(1), Factory);

        Assert.Equal("Keyboard", first.Name);
        Assert.Equal("Keyboard", second.Name);
        Assert.Equal(1, dbCalls);
    }

    [Fact]
    public async Task CacheAside_AbsoluteExpiry_RefetchesAfterDeadline()
    {
        var cache = NewCache();
        var service = new CacheAsideService(cache);
        var dbCalls = 0;

        Task<Product> Factory(CancellationToken ct)
        {
            dbCalls++;
            return Task.FromResult(new Product(1, "Keyboard", "Hardware", 129.99m));
        }

        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(100),
        };

        await service.GetOrCreateAsync(CacheKey.Product(1), Factory, options);
        await Task.Delay(200);
        await service.GetOrCreateAsync(CacheKey.Product(1), Factory, options);

        Assert.Equal(2, dbCalls);
    }

    [Fact]
    public async Task StampedeSafe_FiftyConcurrentRequests_LoadOnlyOnce()
    {
        var cache = NewCache();
        var stampedeSafe = new StampedeSafeCache(cache);
        var repository = new ProductRepository();
        var key = CacheKey.Product(2);

        var tasks = Enumerable.Range(0, 50).Select(_ =>
            stampedeSafe.GetOrCreateAsync(
                key,
                ct => repository.GetByIdAsync(2, ct),
                softTtl: TimeSpan.FromMinutes(5),
                hardTtl: TimeSpan.FromMinutes(10)));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("Noise-Cancelling Headphones", r?.Name));
        Assert.Equal(1, repository.DbHits); // the whole point: 50 callers, 1 DB hit
    }

    [Fact]
    public async Task StampedeSafe_StaleValue_ServedWhileBackgroundRefreshRuns()
    {
        var cache = NewCache();
        var stampedeSafe = new StampedeSafeCache(cache);
        var repository = new ProductRepository();
        var key = CacheKey.Product(3);
        var soft = TimeSpan.FromMilliseconds(100);
        var hard = TimeSpan.FromSeconds(30);

        var first = await stampedeSafe.GetOrCreateAsync(key, ct => repository.GetByIdAsync(3, ct), soft, hard);
        Assert.Equal(1, repository.DbHits);

        await Task.Delay(200); // past soft expiry, well inside hard expiry

        // Must return immediately with the stale value (no 150 ms DB delay)...
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stale = await stampedeSafe.GetOrCreateAsync(key, ct => repository.GetByIdAsync(3, ct), soft, hard);
        sw.Stop();

        Assert.Equal(first?.Name, stale?.Name);
        Assert.True(sw.ElapsedMilliseconds < 100, $"Stale fetch took {sw.ElapsedMilliseconds} ms — it should not wait for the DB.");

        // ...and exactly one background refresh must follow.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (repository.DbHits < 2 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(2, repository.DbHits);
    }

    [Fact]
    public async Task PubSub_Invalidation_EvictsOtherNodesCache()
    {
        var channel = new InMemoryInvalidationChannel();
        var nodeACache = NewCache();
        var nodeA = new CacheAsideService(nodeACache);
        var repository = new ProductRepository();
        var key = CacheKey.Product(1);

        await channel.SubscribeAsync(k => nodeA.RemoveAsync(k));

        await nodeA.GetOrCreateAsync(key, ct => repository.GetByIdAsync(1, ct));
        Assert.Equal(1, repository.DbHits);

        // Cached: a second fetch costs nothing...
        await nodeA.GetOrCreateAsync(key, ct => repository.GetByIdAsync(1, ct));
        Assert.Equal(1, repository.DbHits);

        // ...until another node publishes an invalidation.
        await channel.PublishInvalidationAsync(key);
        await nodeA.GetOrCreateAsync(key, ct => repository.GetByIdAsync(1, ct));
        Assert.Equal(2, repository.DbHits);
    }

    [Fact]
    public void CacheKey_Formats_AreStableAndNormalized()
    {
        Assert.Equal("v1:product:42", CacheKey.Product(42));
        Assert.Equal("v1:products:category:hardware", CacheKey.ProductsByCategory("Hardware"));
        Assert.Equal("v1:products:category:hardware", CacheKey.ProductsByCategory("HARDWARE"));
    }
}
