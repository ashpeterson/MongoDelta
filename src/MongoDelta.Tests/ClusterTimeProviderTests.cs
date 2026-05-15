using EphemeralMongo;
using MongoDB.Driver;

namespace MongoDelta.Tests;

/// <summary>Integration tests — spin up a real mongod replica set via EphemeralMongo.</summary>
public class ClusterTimeProviderTests : IAsyncLifetime
{
    private IMongoRunner _runner = null!;
    private IMongoClient _client = null!;

    public async Task InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync(new MongoRunnerOptions
        {
            UseSingleNodeReplicaSet = true,
        });
        _client = new MongoClient(_runner.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        _runner.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetTimestampAsync_ReturnsNonEmptyString()
    {
        var provider = new ClusterTimeProvider(_client);
        var ts = await provider.GetTimestampAsync();
        Assert.False(string.IsNullOrEmpty(ts));
    }

    [Fact]
    public async Task GetTimestampAsync_FormatIsSecondsAndOrdinal()
    {
        var provider = new ClusterTimeProvider(_client);
        var ts = await provider.GetTimestampAsync();

        // Expected format: "{seconds}.{ordinal}"
        var parts = ts.Split('.');
        Assert.Equal(2, parts.Length);
        Assert.True(long.TryParse(parts[0], out _), $"seconds part not parseable: '{parts[0]}'");
        Assert.True(long.TryParse(parts[1], out _), $"ordinal part not parseable: '{parts[1]}'");
    }

    [Fact]
    public async Task GetTimestampAsync_AdvancesAfterWrite()
    {
        var provider = new ClusterTimeProvider(_client);
        var db  = _client.GetDatabase("integration_test");
        var col = db.GetCollection<MongoDB.Bson.BsonDocument>("col");

        var before = await provider.GetTimestampAsync();
        await col.InsertOneAsync(new MongoDB.Bson.BsonDocument("x", 1));
        var after = await provider.GetTimestampAsync();

        // Parse and compare as (seconds, ordinal) tuples
        var (bs, bo) = ParseTimestamp(before);
        var (as_, ao) = ParseTimestamp(after);
        var afterIsGreater = (as_ > bs) || (as_ == bs && ao > bo);
        Assert.True(afterIsGreater, $"Expected {after} > {before} after a write");
    }

    [Fact]
    public async Task GetTimestampAsync_StableWithNoWrites()
    {
        var provider = new ClusterTimeProvider(_client);
        var db  = _client.GetDatabase("integration_test");
        var col = db.GetCollection<MongoDB.Bson.BsonDocument>("col");

        // Prime the collection
        await col.InsertOneAsync(new MongoDB.Bson.BsonDocument("primed", true));

        var before = await provider.GetTimestampAsync();

        // 20 reads — no writes
        for (int i = 0; i < 20; i++)
            await col.Find(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty).ToListAsync();

        var after = await provider.GetTimestampAsync();

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task GetTimestampAsync_StrictlyMonotonicAcrossCollections()
    {
        var provider = new ClusterTimeProvider(_client);
        var db   = _client.GetDatabase("integration_test");
        var colA = db.GetCollection<MongoDB.Bson.BsonDocument>("mono_a");
        var colB = db.GetCollection<MongoDB.Bson.BsonDocument>("mono_b");

        var timestamps = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            timestamps.Add(await provider.GetTimestampAsync());
            await (i % 2 == 0 ? colA : colB)
                .InsertOneAsync(new MongoDB.Bson.BsonDocument("i", i));
        }
        timestamps.Add(await provider.GetTimestampAsync());

        for (int i = 1; i < timestamps.Count; i++)
        {
            var (ps, po) = ParseTimestamp(timestamps[i - 1]);
            var (cs, co) = ParseTimestamp(timestamps[i]);
            var nonDecreasing = (cs > ps) || (cs == ps && co >= po);
            Assert.True(nonDecreasing,
                $"Timestamp decreased at index {i}: {timestamps[i - 1]} → {timestamps[i]}");
        }
    }

    // ── B4: freshness — clusterTime from hello reflects the latest write ─────

    [Fact]
    public async Task GetTimestampAsync_ReflectsWriteOperationTime()
    {
        // Write a document using a client session so we get the operationTime back.
        var db  = _client.GetDatabase("freshness_test");
        var col = db.GetCollection<MongoDB.Bson.BsonDocument>("freshness_col");

        using var session = await _client.StartSessionAsync();
        await col.InsertOneAsync(session, new MongoDB.Bson.BsonDocument("fresh", 1));

        // operationTime is the timestamp the server assigned to this write.
        var opTime = session.OperationTime!;

        var provider = new ClusterTimeProvider(_client);
        var ts = await provider.GetTimestampAsync();
        var (cs, co) = ParseTimestamp(ts);

        // hello's $clusterTime must be >= the write's operationTime.
        var afterOrEqual = (cs > opTime.Timestamp) ||
                           (cs == opTime.Timestamp && co >= opTime.Increment);
        Assert.True(afterOrEqual,
            $"hello clusterTime {ts} should be >= write operationTime " +
            $"{opTime.Timestamp}.{opTime.Increment}");
    }

    private static (long seconds, long ordinal) ParseTimestamp(string ts)
    {
        var parts = ts.Split('.');
        return (long.Parse(parts[0]), long.Parse(parts[1]));
    }
}
