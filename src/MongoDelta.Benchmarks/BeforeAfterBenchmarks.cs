using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDelta.Benchmarks;

/// <summary>
/// Before/after: measures the same endpoint (findOne + JSON response)
/// with no middleware, with middleware on a cold request, and on a cached 304.
///
/// This isolates the three scenarios a production app actually sees:
///   Baseline  — today, without MongoDelta: findOne every time
///   After/200 — with MongoDelta, data changed (or first request): hello + findOne
///   After/304 — with MongoDelta, data unchanged: hello only, handler skipped
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class BeforeAfterBenchmarks
{
    private const string ConnectionString =
        "mongodb://127.0.0.1:27117/?directConnection=true&replicaSet=singleNodeReplSet";

    // ── Before (no middleware) ────────────────────────────────────────────────

    private IHost _beforeHost = null!;
    private HttpClient _beforeClient = null!;

    // ── After — cold (middleware + data changed / first request) ─────────────

    private IHost _afterHost = null!;
    private HttpClient _afterClient200 = null!;

    // ── After — cached (middleware + data unchanged) ──────────────────────────

    private HttpClient _afterClient304 = null!;
    private string _cachedETag = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var mongoClient = new MongoClient(ConnectionString);
        var db          = mongoClient.GetDatabase("bench_before_after");
        var collection  = db.GetCollection<BsonDocument>("items");

        // Seed a document to find on each request
        await collection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
        await collection.InsertOneAsync(new BsonDocument { ["_id"] = "item1", ["value"] = 42 });

        var provider = new ClusterTimeProvider(mongoClient);

        // ── Before host — plain endpoint, no middleware ───────────────────────
        _beforeHost = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.Configure(app =>
                {
                    app.Run(async ctx =>
                    {
                        var item = await collection
                            .Find(new BsonDocument("_id", "item1"))
                            .FirstOrDefaultAsync();
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(item?.ToJson() ?? "{}");
                    });
                });
            })
            .Start();

        // ── After host — same handler behind MongoDelta ───────────────────────
        _afterHost = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.Configure(app =>
                {
                    app.UseMongoDelta(provider);
                    app.Run(async ctx =>
                    {
                        var item = await collection
                            .Find(new BsonDocument("_id", "item1"))
                            .FirstOrDefaultAsync();
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(item?.ToJson() ?? "{}");
                    });
                });
            })
            .Start();

        _beforeClient    = _beforeHost.GetTestClient();
        _afterClient200  = _afterHost.GetTestClient();
        _afterClient304  = _afterHost.GetTestClient();

        // Capture the current ETag so the 304 client can send a matching If-None-Match
        var warmup = await _afterClient200.GetAsync("/");
        _cachedETag = warmup.Headers.ETag!.ToString();
        _afterClient304.DefaultRequestHeaders.Add("If-None-Match", _cachedETag);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _beforeClient.Dispose();
        _afterClient200.Dispose();
        _afterClient304.Dispose();
        _beforeHost.Dispose();
        _afterHost.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Before: findOne every request (no cache)")]
    public Task<HttpResponseMessage> Before_NoMiddleware() =>
        _beforeClient.GetAsync("/");

    [Benchmark(Description = "After: data changed — hello + findOne (200)")]
    public Task<HttpResponseMessage> After_DataChanged_200() =>
        _afterClient200.GetAsync("/");

    [Benchmark(Description = "After: data unchanged — hello only (304, handler skipped)")]
    public Task<HttpResponseMessage> After_DataUnchanged_304() =>
        _afterClient304.GetAsync("/");
}
