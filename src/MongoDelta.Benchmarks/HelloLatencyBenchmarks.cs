using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using MongoDB.Driver;

namespace MongoDelta.Benchmarks;

/// <summary>
/// B1 — hello command latency overhead.
/// Measures the cost of calling GetTimestampAsync() against a local replica set.
/// Run the dev server first: ./dev-server.sh
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class HelloLatencyBenchmarks
{
    private const string ConnectionString =
        "mongodb://127.0.0.1:27117/?directConnection=true&replicaSet=singleNodeReplSet";

    private IMongoClient _client = null!;
    private ClusterTimeProvider _provider = null!;

    [GlobalSetup]
    public void Setup()
    {
        _client = new MongoClient(ConnectionString);
        _provider = new ClusterTimeProvider(_client);
        // Warm up connection pool
        _provider.GetTimestampAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => (_client as IDisposable)?.Dispose();

    [Benchmark(Description = "hello → $clusterTime (baseline)")]
    public Task<string> HelloCommand() => _provider.GetTimestampAsync();

    [Benchmark(Description = "hello × 10 sequential")]
    public async Task HelloCommand_10x()
    {
        for (int i = 0; i < 10; i++)
            await _provider.GetTimestampAsync();
    }
}
