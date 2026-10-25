namespace RedisDemo.Core;

/// <summary>
/// One place that owns every cache-key format in the app.
///
/// Why this matters: cache keys are a contract. Two callers building the same
/// key by hand will eventually disagree ("product:1" vs "Product:1" vs
/// "products:1"), and then one of them reads stale data forever. A single
/// builder also makes invalidation possible — you can only evict what you
/// can name — and the version prefix lets you invalidate the whole cache
/// by bumping one constant ("v1" → "v2") instead of flushing Redis.
///
/// Conventions used here:
///   - lowercase, colon-separated segments ("product:42", never "Product_42")
///   - a version prefix, so a schema change is a clean break, not a bug
///   - entity ids and normalized categories, never raw user input
/// </summary>
public static class CacheKey
{
    public const string Version = "v1";

    public static string Product(int id) => $"{Version}:product:{id}";

    public static string ProductsByCategory(string category) =>
        $"{Version}:products:category:{category.ToLowerInvariant()}";
}
