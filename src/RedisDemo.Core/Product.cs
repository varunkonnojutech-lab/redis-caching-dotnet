namespace RedisDemo.Core;

/// <summary>
/// The thing being cached: a product row, as it might come back from a database.
/// Kept deliberately small so the caching patterns — not the model — are the lesson.
/// </summary>
public sealed record Product(int Id, string Name, string Category, decimal Price);
