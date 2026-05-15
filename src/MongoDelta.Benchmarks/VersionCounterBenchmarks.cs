using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using MongoDB.Driver;

namespace MongoDelta.Benchmarks;

/// <summary>
/// B3 — Version counter write overhead (standalone fallback).
/// Measures FindOneAndUpdate ($inc, upsert) latency vs baseline insert.
/// Run the dev server first: ./dev-server.sh
/// Note: the dev server is a replica set so $clusterTime is available —
/// this benchmark targets the counter collection directly to measure raw overhead.
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class VersionCounterBenchmarks
{
    private const string ConnectionString =
        "mongodb://127.0.0.1:27117/?directConnection=true&replicaSet=singleNodeReplSet";

    private IMongoClient _client = null!;
    private MongoVersionStore _store = null!;
    private IMongoCollection<MongoDB.Bson.BsonDocument> _baselineCollection = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _client = new MongoClient(ConnectionString);
        _store = new MongoVersionStore(_client, "_mongodelta_bench", "version_bench");
        _baselineCollection = _client
            .GetDatabase("_mongodelta_bench")
            .GetCollection<MongoDB.Bson.BsonDocument>("baseline");

        // Warm up
        await _store.IncrementAsync();
        await _baselineCollection.InsertOneAsync(new MongoDB.Bson.BsonDocument("warmup", 1));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _client.GetDatabase("_mongodelta_bench").DropCollectionAsync("version_bench");
        await _client.GetDatabase("_mongodelta_bench").DropCollectionAsync("baseline");
    }

    [Benchmark(Baseline = true, Description = "Baseline: insertOne (no counter)")]
    public Task BaselineInsert() =>
        _baselineCollection.InsertOneAsync(new MongoDB.Bson.BsonDocument("x", 1));

    [Benchmark(Description = "IncrementAsync ($inc upsert)")]
    public Task Increment() => _store.IncrementAsync();

    [Benchmark(Description = "IncrementAsync + insertOne (full write path)")]
    public async Task IncrementThenInsert()
    {
        await _store.IncrementAsync();
        await _baselineCollection.InsertOneAsync(new MongoDB.Bson.BsonDocument("x", 1));
    }
}
