# Caching in .NET — cache-aside, stampede protection & pub/sub invalidation

[![Build](https://github.com/varunkonnojutech-lab/redis-caching-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/varunkonnojutech-lab/redis-caching-dotnet/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/varunkonnojutech-lab/redis-caching-dotnet)

Every tutorial shows you how to *put something in Redis*. Almost none show you what breaks in production: a popular key expires and 500 requests hit the database at once, or one server updates a product and every other server keeps serving the old one for an hour. This repo is about those two problems — **cache stampedes** and **cache invalidation** — plus the bread-and-butter **cache-aside pattern** that ties them together.

## Who is this for

.NET developers who have used a cache (or `IMemoryCache`) and want the production picture: how to keep a cache correct across servers, how to survive a hot key expiring, and when Redis earns its place over in-memory caching.

## What you'll learn

- **Cache-aside done right** — read-through on miss, write-through on load, and why the code depends on `IDistributedCache` (not Redis) so tests run with zero infrastructure
- **Cache stampedes** — why a hot key expiring is a self-inflicted DDoS on your own database, and how single-flight loading collapses 50 concurrent requests into 1 database call
- **Stale-while-revalidate** — two deadlines per entry (soft + hard expiry) so users get a fast, slightly stale answer while exactly one background refresh runs
- **Pub/sub invalidation** — one channel, key-only messages, so every server evicts a stale key the moment another server publishes — no polling, no TTL guessing
- **Cache-key discipline** — one builder, versioned prefixes, normalized segments: the boring rule that prevents the "two callers, two key formats, one stale forever" bug
- **Expiry strategy** — absolute vs sliding expiration, and when each one is the right call

## Quick start

You need .NET 8. The unit tests run with no Redis at all:

```bash
dotnet test
```

Run all four demos (in-memory stand-ins, no Redis needed):

```bash
dotnet run --project src/RedisDemo.Demo
```

Run one demo on its own:

```bash
dotnet run --project src/RedisDemo.Demo -- stampede
```

For the real thing, start Redis and point the demos at it — the code is identical, only the wiring changes:

```bash
docker compose up -d
REDIS_CONNECTION=localhost:6379 dotnet run --project src/RedisDemo.Demo
```

Stop Redis when you're done: `docker compose down`.

## What the demos show

**1. Cache-aside** — fetch product 1 twice. The first fetch hits the (slow, fake) database; the second returns from cache. Watch the DB-hit counter stay at 1.

**2. Cache stampede** — 50 concurrent requests for the same cold key. The single-flight gate lets exactly one through to the database; the other 49 wait and read what it stored. New DB hits: 1, not 50.

**3. Stale-while-revalidate** — the entry's soft deadline passes, but the hard one hasn't. The fetch returns *immediately* with the stale value (no 150 ms database wait) while one background refresh updates the cache. Users never pay for a refresh.

**4. Pub/sub invalidation** — two "servers" (separate caches, one shared channel). Node A caches a product; node B publishes an invalidation; node A's next fetch goes back to the database. With real Redis, run two demo processes and watch the same thing happen across processes.

## The patterns, in one picture

```
Request → Cache hit (fresh)? ──yes──▶ return it
              │ no
              ▼
         Stale but within hard expiry? ──yes──▶ return stale + refresh in background (one flight only)
              │ no
              ▼
         Cold / fully expired? ──▶ one caller loads from DB, rest wait, all read the stored value
```

And for writes:

```
Server A updates product → publishes "v1:product:42" on cache:invalidation
Server B, C, D subscribed → each evicts "v1:product:42" from its local cache
Next read on any server → cache miss → fresh value from the database
```

## Project layout

```
src/RedisDemo.Core/        # the patterns: CacheAsideService, StampedeSafeCache,
                           #   CacheKey, invalidation channels (in-memory + Redis)
src/RedisDemo.Demo/        # console app running the four demos (memory or real Redis)
tests/RedisDemo.Tests/     # xUnit tests, all against DistributedMemoryCache — no Redis needed
docker-compose.yml         # redis:7-alpine for the real-Redis run
```

## Honest limits

- The stampede gate is **in-process**. Across several app servers, two servers can still load the same cold key at once — fixing that needs a distributed lock (Redis `SET NX` with expiry, or RedLock). This repo names the gap instead of pretending the semaphore solves it.
- The in-memory channel can't reach another server — it's for tests and local demos. Production uses `RedisInvalidationChannel`, same interface.
- A background refresh that throws is swallowed so the stale value stays servable until hard expiry. In production, log it — a refresh that keeps failing is how a cache goes stale for hours unnoticed.

## Next steps

- Add a distributed lock (Redis `SET NX`) to the stampede gate so it holds across servers.
- Try a write-through/write-behind variant and compare it with cache-aside for a write-heavy workload.
- Point the demo at Azure Cache for Redis and measure the latency difference vs local Redis.
- Pair this with the [kafka-dotnet](https://github.com/varunkonnojutech-lab/kafka-dotnet) repo: cache invalidation messages and domain events often travel the same pipes.
