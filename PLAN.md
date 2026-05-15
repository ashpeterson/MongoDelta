# MongoDelta — Implementation Plan

HTTP 304 Not Modified middleware for ASP.NET Core backed by MongoDB, modelled after
[SimonCropp/Delta](https://github.com/SimonCropp/Delta) which does the same for SQL Server
and PostgreSQL.

---

## Core Mechanism (reference)

Delta's trick: instead of hashing response bodies, query the database for a single
monotonic "has anything changed?" counter and use it as an ETag.

```
ETag = "{AssemblyWriteTime}-{DbTimestamp}-{OptionalSuffix}"
```

SQL Server has `@@DBTS` (database-wide rowversion, increments on every write).
MongoDB's closest equivalent: `$clusterTime` returned by the `hello` command on
replica sets.

---

## Phase 0 — Spike (Kill/Pursue Gate)

**Goal:** answer three factual questions before writing any library code.

### Tasks

1. **Measure `hello` command latency** against a local replica set and against Atlas.
   Target: p99 < 2 ms on localhost, p99 < 10 ms on Atlas same-region.

2. **Verify `$clusterTime` advances on every write** across collections in the same
   database. Run concurrent writes on two collections, assert clusterTime after each
   batch is strictly greater than before.

3. **Verify `$clusterTime` does NOT advance on reads alone** (idle cluster). Confirm
   that a cluster receiving only reads holds clusterTime steady — meaning 304s will
   be served correctly when data is unchanged.

4. **Standalone MongoDB check:** confirm `hello` on a standalone mongod returns no
   `$clusterTime`. Document fallback requirement.

### Spike code (minimal, throwaway)

```csharp
// Program.cs — bare-minimum spike
var client = new MongoClient(connectionString);
var db = client.GetDatabase("spike");

// Measure hello latency
var sw = Stopwatch.StartNew();
for (int i = 0; i < 1000; i++)
{
    var result = await db.RunCommandAsync<BsonDocument>(
        new BsonDocument("hello", 1));
    _ = result["$clusterTime"]["clusterTime"].AsBsonTimestamp;
}
Console.WriteLine($"hello p50: {sw.ElapsedMilliseconds / 1000.0:F2} ms");

// Confirm monotonicity across writes
var ts1 = await GetClusterTime(db);
await db.GetCollection<BsonDocument>("col_a")
        .InsertOneAsync(new BsonDocument("x", 1));
var ts2 = await GetClusterTime(db);
await db.GetCollection<BsonDocument>("col_b")
        .InsertOneAsync(new BsonDocument("y", 2));
var ts3 = await GetClusterTime(db);
Debug.Assert(ts2 > ts1 && ts3 > ts2, "clusterTime must be strictly monotonic");
```

### Kill criteria (stop here if any are true)

| Condition | Threshold | Reason to kill |
|---|---|---|
| `hello` p99 latency | > 5 ms on Atlas same-region | overhead exceeds benefit for fast endpoints |
| `$clusterTime` monotonicity | any violation | ETag correctness is impossible without it |
| `$clusterTime` advances on reads | confirmed | all requests would get unique ETags → 304s never served |
| Standalone workaround | adds > 5 ms write-path overhead | version counter write contention too expensive |

### Pursue criteria (all must be true)

- [ ] `hello` p99 < 2 ms localhost, < 10 ms Atlas
- [ ] `$clusterTime` is strictly monotonic across writes on the same replica set
- [ ] `$clusterTime` is stable (unchanged) when no writes occur
- [ ] The C# driver exposes the value without raw BSON parsing (or wrapping it is trivial)

---

## Phase 1 — Core Middleware (MVP)

**Scope:** replica sets and Atlas only. No standalone fallback yet.

### Architecture

```
IMongoTimestampProvider          (abstraction)
  └── ClusterTimeProvider        (hello command → $clusterTime)
  └── VersionCounterProvider     (Phase 2 — standalone fallback)

MongoDeltaMiddleware
  ├── Reads If-None-Match header
  ├── Calls IMongoTimestampProvider.GetTimestampAsync()
  ├── Builds ETag string
  ├── Returns 304 if match, else continues pipeline and sets ETag on response
  └── Respects HTTP spec: only for GET/HEAD, only 2xx responses

MongoDeltaExtensions
  └── app.UseMongoDelta(client, options)
```

### ETag composition

```csharp
// format mirrors Delta's SQL Server format for potential cross-DB consistency
var etag = $"\"{assemblyWriteTime}-{ts.Timestamp}.{ts.Increment}-{suffix}\"";
```

`assemblyWriteTime` = `new FileInfo(Assembly.GetEntryAssembly()!.Location).LastWriteTimeUtc.Ticks`
— ensures a redeploy always invalidates all client caches, identical to Delta's behaviour.

### Registration API (target)

```csharp
// Minimal
app.UseMongoDelta(mongoClient);

// With options
app.UseMongoDelta(mongoClient, options =>
{
    options.Database   = "mydb";          // scope ETag to DB (default: cluster)
    options.Suffix     = ctx => ctx.User.Identity?.Name;  // per-user scoping
    options.OnSkip     = ctx => ctx.Request.Path.StartsWithSegments("/health");
});
```

### Key implementation decisions

| Decision | Choice | Rationale |
|---|---|---|
| Which command to call | `hello` | documented, non-admin, < 1 ms |
| Scope of ETag | cluster-wide by default | matches `@@DBTS` semantics; DB-scoped available via option |
| Connection reuse | reuse app's `IMongoClient` | no extra connection pool |
| Async | fully async throughout | no blocking calls on request path |
| Thread safety | stateless middleware, provider holds no state | trivially safe |

### Deliverables

- [ ] `IMongoTimestampProvider` interface
- [ ] `ClusterTimeProvider` implementation
- [ ] `MongoDeltaMiddleware` (handles ETag comparison, 304, sets response header)
- [ ] `MongoDeltaOptions` (Database, Suffix delegate, OnSkip predicate)
- [ ] `MongoDeltaExtensions.UseMongoDelta()`
- [ ] Unit tests: ETag format, 304 logic, skip predicate, HEAD support
- [ ] Integration test: real mongod replica set via Testcontainers

---

## Phase 2 — Standalone Fallback

**Scope:** single-node `mongod` (dev environments, no replica set).

### Mechanism: version counter collection

```javascript
// _mongodelta_version collection, single document:
{ _id: "v", seq: NumberLong(1042) }
```

**Read:** `findOne({_id: "v"}, {seq: 1})` — O(1), index hit.  
**Increment:** called by application code on every write via a helper, or via a
MongoDB trigger if Atlas is available.

```csharp
// Helper the app calls after any write:
await deltaVersionStore.IncrementAsync(cancellationToken);
// internally: FindOneAndUpdate({_id:"v"}, {$inc:{seq:1}}, upsert:true)
```

### Kill criteria for this phase

- If write-path `findOneAndUpdate` adds > 3 ms p99 latency under realistic load →
  document the limitation and mark standalone as "unsupported for high-write workloads"
  rather than making it the default.

### Deliverables

- [ ] `VersionCounterProvider` implementation
- [ ] `IMongoVersionStore` and default `MongoVersionStore`
- [ ] Auto-detection: if `hello` returns no `$clusterTime`, switch to version counter
- [ ] Benchmark: write throughput with and without counter increment (see Benchmarks section)

---

## Phase 3 — Production Hardening ✅

### Sharded cluster support

On sharded clusters, `$clusterTime` is returned by `mongos` and already represents
the latest time seen across all shards. No special handling needed in the middleware.

**Integration test status:** not automated — EphemeralMongo does not support sharded
clusters and Docker is not available in this environment. The claim is documented in
`ClusterTimeProvider` XML comments. Verify manually against a real sharded cluster
or Atlas before promoting to GA.

### Collection-scoped ETags (optional, deferred)

Cluster-wide ETag is correct and safe (it just over-invalidates). Implement only if
user demand warrants the additional complexity of a change-stream watcher.

### Deliverables

- [x] `ShouldCache` / skip hook — implemented as `MongoDeltaOptions.OnSkip`
- [x] XML doc comments on all public API surface
- [x] NuGet package scaffold (`MongoDelta`) — v0.1.0-alpha, `GenerateDocumentationFile`
- [x] `If-None-Match` wildcard (`*`) and comma-separated list handling (RFC 7232)
- [x] `Cache-Control: no-cache` on 2xx responses (required for browser revalidation)
- [x] `VaryByHeaders` option — emits `Vary` header on 2xx for CDN-safe user-scoped ETags
- [x] `DeploymentVersion` option — overrides assembly write time for container deployments
- [x] Provider exception propagation test
- [x] README with architecture diagram, quick start, options reference, benchmarks, limitations
- [ ] Sharded cluster integration test (blocked: needs Docker or real Atlas)

---

## Benchmarks

Run with [BenchmarkDotNet](https://benchmarkdotnet.org). All measured against a
local 3-node replica set (Docker) and an Atlas M10 in the same region.

### B1 — `hello` command overhead

| Metric | Target | Method |
|---|---|---|
| p50 latency | < 1 ms | 10k iterations, warm connection |
| p99 latency | < 2 ms (local), < 10 ms (Atlas) | same |
| Throughput | > 2000 req/s sustained | concurrent load |

Compare to Delta's SQL Server equivalent:
```
SELECT log_end_lsn FROM sys.dm_db_log_stats(db_id())
```
(typically 0.5–1 ms local).

### B2 — End-to-end 304 vs 200

Measure wall-clock time for an endpoint that returns 10 KB of JSON:

| Scenario | Expected outcome |
|---|---|
| First request (cold ETag) | ~same as baseline + hello latency |
| Repeat request, data unchanged (304) | baseline response time × 0.05–0.2 |
| Repeat request, data changed (200) | ~same as baseline + hello latency |

**Break-even point:** If `hello` costs 2 ms and baseline endpoint costs 5 ms, 304 is
only a win if the response body serialisation + DB query saved > 2 ms. Document this
formula so users can evaluate for their workloads.

### B3 — Version counter write overhead (Phase 2)

| Scenario | Metric | Target |
|---|---|---|
| `findOneAndUpdate` ($inc, upsert) solo | p99 latency | < 2 ms local |
| 500 concurrent writers | throughput drop vs baseline | < 10% |
| 2000 concurrent writers | throughput drop vs baseline | document, don't target |

If B3 shows meaningful contention, consider batching increments (accumulate writes
in memory for 10 ms, flush once) — but only if contention is real.

### B4 — `clusterTime` freshness under high write load

Confirm that `clusterTime` from `hello` reflects the very latest write within one
round-trip. Run: write → hello → assert returned timestamp ≥ write's operationTime.
This is a correctness benchmark, not a performance one.

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| `$clusterTime` not available in older MongoDB (< 3.6) | Low (3.6 is EOL) | Medium | Document minimum version: MongoDB 4.4+ |
| Atlas free tier throttles `hello` calls | Low | Low | Test on M0; document any limits |
| `clusterTime` clock skew on failover | Low | Medium | ETag mismatch = 200 (not 304) — safe, just suboptimal |
| Users bypass write helper in standalone mode | Medium | High | Log a warning when seq hasn't changed for > 60s but reads are active |
| Conflict with other ETags middleware | Medium | Medium | Check for existing ETag header before setting; skip if present |

---

## Timeline Estimate

| Phase | Effort | Gate |
|---|---|---|
| Phase 0 — Spike | 1–2 days | Kill/pursue decision |
| Phase 1 — Core MVP | 3–5 days | Working 304 on replica set |
| Phase 2 — Standalone | 2–3 days | All deployment modes covered |
| Phase 3 — Hardening | 3–5 days | NuGet-ready |
| Benchmarks (all) | 1–2 days | Alongside Phase 1–2 |
| **Total** | **~2–3 weeks** | |

---

## Reference

- [SimonCropp/Delta](https://github.com/SimonCropp/Delta) — the SQL Server/Postgres original
- [MongoDB `hello` command](https://www.mongodb.com/docs/manual/reference/command/hello/)
- [MongoDB Change Streams / clusterTime](https://www.mongodb.com/docs/manual/changestreams/)
- [adibradfield/MongoDelta](https://github.com/adibradfield/MongoDelta) — unrelated (write-side change tracking)
- [CacheCow](https://github.com/aliostad/CacheCow) — HTTP caching middleware, no Mongo integration
