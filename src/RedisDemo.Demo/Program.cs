using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using RedisDemo.Core;

/// <summary>
/// Runs the four demos. Usage:
///   dotnet run --project src/RedisDemo.Demo            (all demos)
///   dotnet run --project src/RedisDemo.Demo -- stampede
///
/// By default everything runs against in-memory stand-ins, so no Redis is
/// needed. Set REDIS_CONNECTION (e.g. "localhost:6379") to run the same
/// demos against a real Redis — start one with `docker compose up -d`.
/// </summary>
var redisConnection = Environment.GetEnvironmentVariable("REDIS_CONNECTION");

IDistributedCache cache;
ICacheInvalidationChannel channel;

if (redisConnection is not null)
{
    Console.WriteLine($"[setup] Using real Redis at {redisConnection}");
    cache = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = redisConnection }));
    channel = new RedisInvalidationChannel(redisConnection);
}
else
{
    Console.WriteLine("[setup] No REDIS_CONNECTION set — using in-memory cache + channel.");
    Console.WriteLine("[setup] For real Redis: docker compose up -d, then REDIS_CONNECTION=localhost:6379 dotnet run --project src/RedisDemo.Demo");
    cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    channel = new InMemoryInvalidationChannel();
}

var repository = new ProductRepository();
var cacheAside = new CacheAsideService(cache);
var stampedeSafe = new StampedeSafeCache(cache);

var demos = args.Length == 0 ? new[] { "cache-aside", "stampede", "stale", "pubsub" } : args;

foreach (var demo in demos)
{
    switch (demo)
    {
        case "cache-aside": await CacheAsideDemo(); break;
        case "stampede": await StampedeDemo(); break;
        case "stale": await StaleWhileRevalidateDemo(); break;
        case "pubsub": await PubSubDemo(); break;
        default: Console.WriteLine($"Unknown demo: {demo} (try: cache-aside, stampede, stale, pubsub)"); break;
    }
}

if (channel is IAsyncDisposable asyncDisposable)
{
    await asyncDisposable.DisposeAsync();
}
if (cache is IDisposable disposableCache)
{
    disposableCache.Dispose();
}
return;

async Task CacheAsideDemo()
{
    Console.WriteLine("\n=== 1. Cache-aside: first request hits the DB, second doesn't ===");
    var key = CacheKey.Product(1);

    var first = await cacheAside.GetOrCreateAsync(key, ct => repository.GetByIdAsync(1, ct));
    Console.WriteLine($"First fetch:  {first?.Name} (DB hits so far: {repository.DbHits})");

    var second = await cacheAside.GetOrCreateAsync(key, ct => repository.GetByIdAsync(1, ct));
    Console.WriteLine($"Second fetch: {second?.Name} (DB hits so far: {repository.DbHits})  <-- same value, no new DB hit");
}

async Task StampedeDemo()
{
    Console.WriteLine("\n=== 2. Cache stampede: 50 concurrent requests, 1 DB hit ===");
    var key = CacheKey.Product(2);
    var before = repository.DbHits;

    var tasks = Enumerable.Range(0, 50).Select(_ =>
        stampedeSafe.GetOrCreateAsync(key, ct => repository.GetByIdAsync(2, ct), softTtl: TimeSpan.FromMinutes(5), hardTtl: TimeSpan.FromMinutes(10)));
    var results = await Task.WhenAll(tasks);

    Console.WriteLine($"50 concurrent fetches returned '{results[0]?.Name}' — new DB hits: {repository.DbHits - before}");
    Console.WriteLine("Without single-flight, all 50 would have hit the database at once.");
}

async Task StaleWhileRevalidateDemo()
{
    Console.WriteLine("\n=== 3. Stale-while-revalidate: stale answers beat slow answers ===");
    var key = CacheKey.Product(3);
    var soft = TimeSpan.FromSeconds(2);
    var hard = TimeSpan.FromSeconds(30);

    _ = await stampedeSafe.GetOrCreateAsync(key, ct => repository.GetByIdAsync(3, ct), soft, hard);
    Console.WriteLine($"Cached. Waiting {soft.TotalSeconds}s for the soft deadline to pass...");
    await Task.Delay(soft + TimeSpan.FromMilliseconds(250));

    var before = repository.DbHits;
    var stale = await stampedeSafe.GetOrCreateAsync(key, ct => repository.GetByIdAsync(3, ct), soft, hard);
    Console.WriteLine($"Fetched immediately (no 150 ms wait): {stale?.Name} — served stale while a background refresh runs.");
    await Task.Delay(500); // let the background refresh finish
    Console.WriteLine($"Background refresh done — new DB hits: {repository.DbHits - before}");
}

async Task PubSubDemo()
{
    Console.WriteLine("\n=== 4. Pub/sub invalidation: one node evicts another node's cache ===");

    // Two "servers": separate caches, one shared channel.
    var nodeACache = redisConnection is not null
        ? (IDistributedCache)new RedisCache(Options.Create(new RedisCacheOptions { Configuration = redisConnection }))
        : new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    var nodeA = new CacheAsideService(nodeACache);
    var key = CacheKey.Product(1);

    // Node A subscribes: on invalidation, drop the key from its own cache.
    await channel.SubscribeAsync(k => nodeA.RemoveAsync(k));

    var cached = await nodeA.GetOrCreateAsync(key, ct => repository.GetByIdAsync(1, ct));
    Console.WriteLine($"Node A cached: {cached?.Name} (DB hits: {repository.DbHits})");

    // Node B (this process, acting as the writer) announces the key is stale.
    Console.WriteLine("Node B publishes an invalidation for the same key...");
    await channel.PublishInvalidationAsync(key);
    await Task.Delay(200); // let the subscriber run

    var before = repository.DbHits;
    _ = await nodeA.GetOrCreateAsync(key, ct => repository.GetByIdAsync(1, ct));
    Console.WriteLine($"Node A re-fetched after invalidation — new DB hits: {repository.DbHits - before} (1 = eviction worked)");

    if (nodeACache is IDisposable d) d.Dispose();
}
