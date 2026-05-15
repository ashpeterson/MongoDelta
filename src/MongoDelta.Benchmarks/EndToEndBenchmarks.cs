using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace MongoDelta.Benchmarks;

/// <summary>
/// B2 — End-to-end 304 vs 200 latency via TestServer.
/// Measures the wall-clock difference between:
///   (a) Cold request — no If-None-Match, server returns 200 + body + ETag
///   (b) 304 request — matching If-None-Match, server returns 304 immediately
///   (c) Stale request — non-matching If-None-Match, server returns 200 + fresh ETag
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class EndToEndBenchmarks
{
    private const string ConnectionString =
        "mongodb://127.0.0.1:27117/?directConnection=true&replicaSet=singleNodeReplSet";

    private IHost _host = null!;
    private HttpClient _client200 = null!;
    private HttpClient _client304 = null!;
    private HttpClient _clientStale = null!;
    private string _currentETag = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var mongoClient = new MongoClient(ConnectionString);
        var provider = new ClusterTimeProvider(mongoClient);

        _host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.Configure(app =>
                {
                    app.UseMongoDelta(provider);
                    app.Run(async ctx =>
                    {
                        ctx.Response.ContentType = "application/json";
                        // Simulate a modest 1 KB JSON response
                        await ctx.Response.WriteAsync(new string('x', 1024));
                    });
                });
            })
            .Start();

        // Warm up and capture the current ETag
        var warmup = await _host.GetTestClient().GetAsync("/");
        _currentETag = warmup.Headers.ETag!.ToString();

        _client200   = _host.GetTestClient();
        _client304   = _host.GetTestClient();
        _clientStale = _host.GetTestClient();
        _client304.DefaultRequestHeaders.Add("If-None-Match", _currentETag);
        _clientStale.DefaultRequestHeaders.Add("If-None-Match", "\"stale-etag-bench\"");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client200.Dispose();
        _client304.Dispose();
        _clientStale.Dispose();
        _host.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Cold 200 (no If-None-Match)")]
    public Task<HttpResponseMessage> Cold200() => _client200.GetAsync("/");

    [Benchmark(Description = "304 (matching If-None-Match)")]
    public Task<HttpResponseMessage> HotNotModified304() => _client304.GetAsync("/");

    [Benchmark(Description = "Stale 200 (non-matching ETag)")]
    public Task<HttpResponseMessage> StaleGet200() => _clientStale.GetAsync("/");
}
