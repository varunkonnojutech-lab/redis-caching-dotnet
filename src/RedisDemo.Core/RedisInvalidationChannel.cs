using StackExchange.Redis;

namespace RedisDemo.Core;

/// <summary>
/// Redis pub/sub invalidation: one channel ("cache:invalidation"), key-only
/// messages. Any server subscribed to the channel evicts the key from its
/// local cache the moment another server publishes.
///
/// Uses StackExchange.Redis directly rather than <c>IDistributedCache</c>,
/// because the distributed-cache abstraction has no pub/sub API — this is
/// the standard reason real code keeps a Redis multiplexer around alongside
/// the cache abstraction.
/// </summary>
public sealed class RedisInvalidationChannel : ICacheInvalidationChannel, IAsyncDisposable
{
    private const string ChannelName = "cache:invalidation";

    private readonly IConnectionMultiplexer _multiplexer;

    public RedisInvalidationChannel(string connectionString) =>
        _multiplexer = ConnectionMultiplexer.Connect(connectionString);

    public Task PublishInvalidationAsync(string cacheKey, CancellationToken ct = default) =>
        _multiplexer.GetSubscriber().PublishAsync(RedisChannel.Literal(ChannelName), cacheKey);

    public async Task SubscribeAsync(Func<string, Task> onInvalidated, CancellationToken ct = default)
    {
        var subscriber = _multiplexer.GetSubscriber();
        await subscriber.SubscribeAsync(
            RedisChannel.Literal(ChannelName),
            async (_, value) => await onInvalidated(value!));
    }

    public async ValueTask DisposeAsync()
    {
        await _multiplexer.CloseAsync();
        _multiplexer.Dispose();
    }
}
