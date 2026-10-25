namespace RedisDemo.Core;

/// <summary>
/// In-process pub/sub: every subscriber in this process gets the message.
/// This is what the tests and the no-Redis demo use. It is <em>not</em> a
/// substitute for Redis in production — it cannot reach another server —
/// but it keeps the pattern testable without infrastructure, and it makes
/// the multi-node demo runnable on one machine.
/// </summary>
public sealed class InMemoryInvalidationChannel : ICacheInvalidationChannel
{
    private event Func<string, Task>? Invalidated;

    public Task PublishInvalidationAsync(string cacheKey, CancellationToken ct = default)
    {
        var handlers = Invalidated?.GetInvocationList()
            .Cast<Func<string, Task>>()
            .ToArray() ?? [];
        return Task.WhenAll(handlers.Select(h => h(cacheKey)));
    }

    public Task SubscribeAsync(Func<string, Task> onInvalidated, CancellationToken ct = default)
    {
        Invalidated += onInvalidated;
        return Task.CompletedTask;
    }
}
