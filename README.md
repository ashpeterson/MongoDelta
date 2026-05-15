# MongoDelta

HTTP 304 Not Modified middleware for ASP.NET Core backed by MongoDB. Inspired by [SimonCropp/Delta](https://github.com/SimonCropp/Delta) which does the same for SQL Server and PostgreSQL.

Instead of hashing response bodies, MongoDelta queries the database for a single monotonic "has anything changed?" counter and uses it as the ETag. This means 304 detection costs one lightweight command per request, with zero changes to your existing handlers.

---

## Before / After

### The code change

```csharp
// ── BEFORE ──────────────────────────────────────────────────────────────────
// No HTTP caching. Every GET hits MongoDB and serialises the full response,
// even when nothing has changed since the client's last request.

app.MapGet("/products/{id}", async (string id, IMongoCollection<Product> col) =>
{
    var product = await col.Find(p => p.Id == id).FirstOrDefaultAsync();
    return product is null ? Results.NotFound() : Results.Ok(product);
});


// ── AFTER ────────────────────────────────────────────────────────────────────
// Add one line before your route registrations.
// Your handlers are completely unchanged.

app.UseMongoDelta(mongoClient);   // ← only change

app.MapGet("/products/{id}", async (string id, IMongoCollection<Product> col) =>
{
    var product = await col.Find(p => p.Id == id).FirstOrDefaultAsync();
    return product is null ? Results.NotFound() : Results.Ok(product);
});
```

### What happens on each request

**Scenario 1 — first request (cold cache)**

```
Client                          Server                          MongoDB
  │                               │                               │
  │── GET /products/1 ───────────►│                               │
  │   (no If-None-Match)          │── hello ──────────────────────►│
  │                               │◄── $clusterTime: 1748000042.1 ─│
  │                               │                               │
  │                               │  ETag not in request → run handler
  │                               │── find({_id:"1"}) ────────────►│
  │                               │◄── {name:"Widget", price:9.99}─│
  │                               │                               │
  │◄── 200 OK ────────────────────│                               │
  │    ETag: "...-1748000042.1"   │                               │
  │    Cache-Control: no-cache    │                               │
  │    Body: {name:"Widget"...}   │                               │
```

**Scenario 2 — repeat request, data unchanged (cache hit → 304)**

```
Client                          Server                          MongoDB
  │                               │                               │
  │── GET /products/1 ───────────►│                               │
  │   If-None-Match:              │── hello ──────────────────────►│
  │     "...-1748000042.1"        │◄── $clusterTime: 1748000042.1 ─│
  │                               │                               │
  │                               │  ETag matches → short-circuit
  │                               │  handler never runs
  │                               │  find() never called
  │                               │                               │
  │◄── 304 Not Modified ──────────│                               │
  │    (no body)                  │                               │
```

**Scenario 3 — repeat request, data changed (cache miss → 200)**

```
Client                          Server                          MongoDB
  │                               │                               │
  │── GET /products/1 ───────────►│                               │
  │   If-None-Match:              │── hello ──────────────────────►│
  │     "...-1748000042.1"        │◄── $clusterTime: 1748000099.3 ─│
  │                               │     (advanced — a write happened)
  │                               │                               │
  │                               │  ETag mismatch → run handler
  │                               │── find({_id:"1"}) ────────────►│
  │                               │◄── {name:"Widget", price:8.99}─│
  │                               │     (new price)               │
  │◄── 200 OK ────────────────────│                               │
  │    ETag: "...-1748000099.3"   │                               │
  │    Body: {name:"Widget"...}   │                               │
```

### Measured difference (localhost, single-node replica set)

Benchmark: endpoint does a real `findOne` and writes a JSON response body.

| Scenario | Mean | vs no-cache baseline |
|---|---|---|
| **Before** — no middleware, `findOne` every request | 504 µs | — |
| **After** — data changed (`hello` + `findOne`, 200) | 974 µs | **+93%** |
| **After** — data unchanged (`hello` only, 304) | 457 µs | **−9%** |

The 304 path is 9% _faster_ than the uncached baseline because it skips `findOne` and JSON serialisation entirely, paying only the `hello` command (~300 µs loopback).

The 200 path on a cache miss costs ~470 µs extra (double round-trip to MongoDB). MongoDelta is a net win whenever enough requests are cache hits to offset those misses.

**Break-even hit rate formula:**

```
MongoDelta wins when:   cache_hit_rate  >  hello_latency / handler_latency
```

| hello latency | handler latency | break-even hit rate |
|---|---|---|
| 300 µs (localhost) | 500 µs | 60% |
| 2 ms (Atlas same-region) | 5 ms | 40% |
| 2 ms (Atlas same-region) | 20 ms | 10% |
| 5 ms (Atlas cross-region) | 20 ms | 25% |

For read-heavy APIs where data changes infrequently (e.g., product catalogues, config endpoints, reference data), hit rates of 80–99% are typical — well above every break-even threshold in the table.

---

## How it works

```
ETag = "{deploymentToken}-{dbTimestamp}-{optionalSuffix}"
```

| Deployment mode | Timestamp source | Advances when |
|---|---|---|
| Replica set / Atlas / Sharded | `$clusterTime` from `hello` command | Any write anywhere in the cluster |
| Standalone mongod | Counter document (`$inc` upsert) | You call `IncrementAsync()` |

Auto-detection runs once at startup — no configuration required for replica sets.

---

## Quick start

```csharp
// Program.cs
var mongoClient = new MongoClient(connectionString);

// Replica set / Atlas — one line, no other changes
app.UseMongoDelta(mongoClient);
```

For standalone mongod, the extension returns a store you must call after writes:

```csharp
var (_, store) = app.UseMongoDelta(mongoClient);

// In your write handlers:
await collection.InsertOneAsync(doc);
await store!.IncrementAsync();  // signals that data changed
```

---

## Options

```csharp
app.UseMongoDelta(mongoClient, options =>
{
    // Scope ETag to a specific database (default: cluster-wide)
    options.Database = "mydb";

    // Per-user/tenant suffix — prevents CDN serving one user's cache to another
    options.Suffix = ctx => ctx.User.Identity?.Name;

    // Emit Vary header when using per-user suffix (required for CDN correctness)
    options.VaryByHeaders = ["Authorization"];

    // Skip middleware for specific routes (health checks, upload endpoints, etc.)
    options.OnSkip = ctx => ctx.Request.Path.StartsWithSegments("/health");

    // Override assembly write time for container deployments where timestamps
    // differ between pods — set to your image tag or git SHA
    options.DeploymentVersion = Environment.GetEnvironmentVariable("IMAGE_TAG");

    // Standalone mode: collection where the version counter lives
    options.VersionCounterDatabase   = "_mongodelta";
    options.VersionCounterCollection = "version";
});
```

---

## All benchmarks

Measured on an Intel Core i7-4600U, local single-node replica set, .NET 10, BenchmarkDotNet `ShortRun` job.

### B1 — hello command latency

| Method | Mean | Allocated |
|---|---|---|
| `hello → $clusterTime` (single call) | **300 µs** | 28 KB |
| `hello × 10` sequential | 2,667 µs | 283 KB |

The `hello` command runs in ~300 µs on localhost (loopback). On Atlas same-region you should expect ~1–5 ms. This is the per-request overhead added to every GET/HEAD that goes through MongoDelta.

### B2 — Middleware overhead (no handler work)

| Scenario | Mean | vs baseline |
|---|---|---|
| Cold 200 (no `If-None-Match`) | 421 µs | 1.00× |
| Stale 200 (non-matching ETag) | 505 µs | 1.21× |
| 304 (matching `If-None-Match`) | 516 µs | 1.23× |

This measures the raw middleware cost via TestServer with a trivial 1 KB handler (no DB). All three scenarios pay the same `hello` latency; the small differences are noise at this scale.

### B3 — Before/after with real handler (findOne)

| Scenario | Mean | vs no-cache baseline |
|---|---|---|
| Before — no middleware, `findOne` every time | 504 µs | — |
| After — data changed (`hello` + `findOne`, 200) | 974 µs | +93% |
| After — data unchanged (`hello` only, 304) | **457 µs** | **−9%** |

### B4 — Version counter write overhead (standalone fallback)

| Method | Mean | vs baseline |
|---|---|---|
| `insertOne` baseline (no counter) | 9.8 ms | 1.00× |
| `IncrementAsync` ($inc upsert) | 12.1 ms | 1.24× |
| `IncrementAsync` + `insertOne` (full write path) | 21.3 ms | 2.17× |

The standalone counter adds ~2–3 ms per write on localhost. For high-write workloads (> 500 req/s) on standalone mongod, consider using a replica set instead.

---

## Architecture

```
Request
  │
  ▼
MongoDeltaMiddleware
  ├─ Skip? (non-GET/HEAD, OnSkip predicate) → pass through unchanged
  ├─ Call IMongoTimestampProvider.GetTimestampAsync()
  │    ├─ ClusterTimeProvider    → hello command → $clusterTime
  │    └─ VersionCounterProvider → findOne({_id:"v"}) → seq
  ├─ Build ETag: "{deploymentToken}-{timestamp}-{suffix}"
  ├─ If-None-Match matches current ETag? → 304, stop
  └─ Register OnStarting callback:
       if 2xx: set ETag header, Cache-Control: no-cache, Vary
  │
  ▼
Your handler (only reached if ETag didn't match)
```

Auto-detection at startup:

```
UseMongoDelta(mongoClient)
  └─ run hello synchronously
       ├─ $clusterTime present → ClusterTimeProvider (replica set / Atlas)
       └─ absent or error     → VersionCounterProvider + MongoVersionStore (standalone)
```

---

## Limitations

- **Standalone mode requires explicit signalling.** Writes by other services, direct mongosh inserts, or migration scripts bypass the counter silently. Use a replica set for production to avoid this class of problem.
- **Cluster-wide ETag over-invalidates.** Any write anywhere in the cluster invalidates cached responses for all endpoints. Collection-scoped ETags are possible via change streams but not implemented — the overhead vs benefit trade-off rarely justifies it for typical APIs.
- **Minimum MongoDB version: 4.4.** `$clusterTime` was introduced in 3.6 but the C# driver targeting and the `hello` command name require 4.4+.
- **Sharded clusters:** `mongos` returns a `$clusterTime` reflecting the highest time seen across all shards — no special handling required. Verified by code inspection; automated integration tests require Docker which is not available in all environments.

---

## Requirements

- .NET 8 or later (tested on .NET 10)
- MongoDB 4.4+ (replica set or Atlas for zero-config; standalone with manual `IncrementAsync` calls)
- `MongoDB.Driver` 3.x

## License

MIT
