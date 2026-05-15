using System.Diagnostics;
using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;

// ── console helpers ───────────────────────────────────────────────────────────
const string Green  = "[32m";
const string Red    = "[31m";
const string Yellow = "[33m";
const string Cyan   = "[36m";
const string Bold   = "[1m";
const string Reset  = "[0m";

void Warn(string msg)   => Console.WriteLine($"  {Yellow}⚠ WARN{Reset}  {msg}");
void Info(string msg)   => Console.WriteLine($"  {Cyan}→{Reset} {msg}");
void Header(string msg) => Console.WriteLine($"\n{Bold}{Cyan}── {msg} {Reset}");

var results = new List<(string Name, bool Passed, string Detail)>();
void Record(string name, bool ok, string detail = "")
{
    results.Add((name, ok, detail));
    var marker = ok ? $"{Green}✓ PASS{Reset}" : $"{Red}✗ FAIL{Reset}";
    Console.WriteLine($"  {marker}  {name}" + (detail == "" ? "" : $"  [{detail}]"));
}

// ─────────────────────────────────────────────────────────────────────────────
// 1. REPLICA SET
// ─────────────────────────────────────────────────────────────────────────────

Header("Starting single-node replica set (EphemeralMongo 8)");
Console.WriteLine("  (first run downloads mongod binary — may take a minute)\n");

using var rsRunner = await MongoRunner.RunAsync(new MongoRunnerOptions
{
    UseSingleNodeReplicaSet = true,
});
Info($"ConnectionString: {rsRunner.ConnectionString}");

var client = new MongoClient(rsRunner.ConnectionString);
var db     = client.GetDatabase("spike");

async Task<BsonTimestamp?> GetClusterTime(IMongoDatabase d)
{
    var r = await d.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
    return r.Contains("$clusterTime")
        ? r["$clusterTime"]["clusterTime"].AsBsonTimestamp
        : null;
}

string Ts(BsonTimestamp t) => $"Timestamp({t.Timestamp}, {t.Increment})";

// ─── Check 1: $clusterTime present ───────────────────────────────────────────

Header("Check 1 — $clusterTime present on replica set");

var initialTs = await GetClusterTime(db);
if (initialTs is not null)
{
    Info($"Value: {Ts(initialTs)}");
    Record("$clusterTime present on replica set", true);
}
else
{
    Record("$clusterTime present on replica set", false, "field missing");
}

// ─── Check 2: hello latency ───────────────────────────────────────────────────

Header("Check 2 — hello command latency (1000 iterations, warm)");

for (int i = 0; i < 20; i++)
    await db.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));

var latencies = new long[1000];
for (int i = 0; i < 1000; i++)
{
    var sw = Stopwatch.StartNew();
    await db.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
    latencies[i] = sw.ElapsedMilliseconds;
}
Array.Sort(latencies);

double avg = latencies.Average();
long p50   = latencies[500];
long p95   = latencies[950];
long p99   = latencies[990];
long p999  = latencies[999];

Info($"avg={avg:F2}ms  p50={p50}ms  p95={p95}ms  p99={p99}ms  p99.9={p999}ms");
Record("hello p99 ≤ 5ms (local loopback)", p99 <= 5, $"p99={p99}ms");

// ─── Check 3: monotonicity ────────────────────────────────────────────────────

Header("Check 3 — $clusterTime monotonicity across two collections");

var colA = db.GetCollection<BsonDocument>("col_a");
var colB = db.GetCollection<BsonDocument>("col_b");

var ts_0 = await GetClusterTime(db);
await colA.InsertOneAsync(new BsonDocument("x", 1));
var ts_1 = await GetClusterTime(db);
await colB.InsertOneAsync(new BsonDocument("y", 2));
var ts_2 = await GetClusterTime(db);
await colA.InsertOneAsync(new BsonDocument("x", 3));
var ts_3 = await GetClusterTime(db);
await colB.UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("u", true));
var ts_4 = await GetClusterTime(db);
await colA.DeleteOneAsync(new BsonDocument("x", 1));
var ts_5 = await GetClusterTime(db);

Info($"initial      : {Ts(ts_0!)}");
Info($"after A.insert: {Ts(ts_1!)}  delta={(ts_1!.Value - ts_0!.Value)}");
Info($"after B.insert: {Ts(ts_2!)}  delta={(ts_2!.Value - ts_1!.Value)}");
Info($"after A.insert: {Ts(ts_3!)}  delta={(ts_3!.Value - ts_2!.Value)}");
Info($"after B.update: {Ts(ts_4!)}  delta={(ts_4!.Value - ts_3!.Value)}");
Info($"after A.delete: {Ts(ts_5!)}  delta={(ts_5!.Value - ts_4!.Value)}");

bool nonDecreasing =
    ts_1!.Value >= ts_0!.Value && ts_2!.Value >= ts_1.Value &&
    ts_3!.Value >= ts_2.Value  && ts_4!.Value >= ts_3.Value &&
    ts_5!.Value >= ts_4.Value;

bool strictlyGreater =
    ts_1.Value > ts_0.Value && ts_3.Value > ts_2.Value && ts_5.Value > ts_4.Value;

Record("Non-decreasing after every write (insert/update/delete)", nonDecreasing);
Record("Strictly greater after each write", strictlyGreater);

// ─── Check 4: stability under reads ──────────────────────────────────────────

Header("Check 4 — clusterTime stability during read-only workload");

var tsReadStart = await GetClusterTime(db);
for (int i = 0; i < 50; i++)
    await colA.Find(new BsonDocument()).ToListAsync();
var tsAfterReads = await GetClusterTime(db);

await Task.Delay(500);
var tsAfterIdle = await GetClusterTime(db);

Info($"before reads : {Ts(tsReadStart!)}");
Info($"after  reads : {Ts(tsAfterReads!)}");
Info($"after 500ms  : {Ts(tsAfterIdle!)}");

bool stableUnderReads = tsAfterReads!.Value == tsReadStart!.Value;
bool stableWhileIdle  = tsAfterIdle!.Value  == tsAfterReads.Value;

if (!stableUnderReads)
    Warn("clusterTime advanced during reads — likely replica-set heartbeat, not data change");
if (!stableWhileIdle)
    Warn("clusterTime advanced during 500ms idle — replica-set heartbeat advances it periodically");

// Note: instability here is a WARN not a KILL — 304s remain correct, just slightly
// over-invalidated on heartbeat ticks (typically every 2s on a single-node RS).
// Delta's SQL @@DBTS only advances on actual writes, so this is a meaningful difference.
Record("Stable under reads", stableUnderReads, stableUnderReads ? "" : "heartbeat likely");
Record("Stable during 500ms idle", stableWhileIdle, stableWhileIdle ? "" : "heartbeat advances it");

// ─── Check 5: ETag construction ───────────────────────────────────────────────

Header("Check 5 — ETag string construction");

var assemblyTimeTicks = new FileInfo(typeof(object).Assembly.Location).LastWriteTimeUtc.Ticks;
var latestTs = await GetClusterTime(db);
var etag = $"\"{assemblyTimeTicks}-{latestTs!.Timestamp}.{latestTs.Increment}\"";
Info($"Sample ETag: {etag}");
Record("ETag string well-formed", etag.StartsWith('"') && etag.EndsWith('"'));

// ─────────────────────────────────────────────────────────────────────────────
// 2. STANDALONE
// ─────────────────────────────────────────────────────────────────────────────

Header("Check 6 — standalone mongod: $clusterTime absent (fallback required)");

using var standaloneRunner = await MongoRunner.RunAsync(new MongoRunnerOptions
{
    UseSingleNodeReplicaSet = false,
});
var standaloneDb    = new MongoClient(standaloneRunner.ConnectionString).GetDatabase("spike");
var standaloneHello = await standaloneDb.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
bool noClusterTime  = !standaloneHello.Contains("$clusterTime");

Info($"hello keys: [{string.Join(", ", standaloneHello.Names)}]");
Record("$clusterTime absent on standalone (confirms fallback needed)", noClusterTime);

// ─────────────────────────────────────────────────────────────────────────────
// VERDICT
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine(new string('═', 62));
Console.WriteLine($"{Bold}  SPIKE VERDICT{Reset}");
Console.WriteLine(new string('═', 62));

foreach (var (name, ok, detail) in results)
{
    var m = ok ? $"{Green}✓{Reset}" : $"{Red}✗{Reset}";
    Console.WriteLine($"  {m}  {name}" + (detail == "" ? "" : $"  [{detail}]"));
}

Console.WriteLine();

// Critical kill criteria from PLAN.md
bool criticalOk = results
    .Where(r => r.Name is
        "$clusterTime present on replica set"                        or
        "Non-decreasing after every write (insert/update/delete)"    or
        "Strictly greater after each write"                          or
        "hello p99 ≤ 5ms (local loopback)")
    .All(r => r.Passed);

if (criticalOk)
{
    Console.WriteLine($"{Bold}{Green}  PURSUE ✓{Reset}  All critical criteria met.");
    Console.WriteLine("  Phase 1 (core middleware) is unblocked.");
    Console.WriteLine("  Review any WARN items above and note in the plan.");
}
else
{
    Console.WriteLine($"{Bold}{Red}  KILL ✗{Reset}  One or more critical criteria failed.");
    Console.WriteLine("  Investigate before writing library code.");
}
Console.WriteLine();
