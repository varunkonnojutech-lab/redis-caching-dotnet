namespace RedisDemo.Core;

/// <summary>
/// A stand-in for a real database: slow on purpose (150 ms per call) and
/// counting every hit, so the demos and tests can <em>prove</em> how many
/// database calls a caching strategy actually makes. In production this is
/// your EF Core repository; the caching code never knows the difference.
/// </summary>
public sealed class ProductRepository
{
    private readonly Dictionary<int, Product> _store = new()
    {
        [1] = new Product(1, "Mechanical Keyboard", "Hardware", 129.99m),
        [2] = new Product(2, "Noise-Cancelling Headphones", "Audio", 249.00m),
        [3] = new Product(3, "4K Monitor", "Hardware", 399.99m),
    };

    private int _dbHits;

    /// <summary>How many times the "database" has been hit. The demos print this.</summary>
    public int DbHits => _dbHits;

    public async Task<Product?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _dbHits);
        await Task.Delay(150, ct); // pretend the database is slow
        return _store.TryGetValue(id, out var product) ? product : null;
    }
}
