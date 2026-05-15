using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDelta;

/// <summary>
/// Reads a monotonically increasing counter from a single MongoDB document and
/// returns it as a decimal string. Used as the timestamp source on standalone
/// mongod instances that do not expose <c>$clusterTime</c>.
/// </summary>
/// <remarks>
/// The counter document lives in the <c>_mongodelta.version</c> collection by
/// default (configurable). It is created automatically on the first call to
/// <see cref="MongoVersionStore.IncrementAsync"/>.
/// <para>
/// Returns <c>"0"</c> when no document exists yet (before any writes have been
/// signalled), which is a safe initial state — all clients will receive a 200
/// on their first request and cache the ETag from that response.
/// </para>
/// </remarks>
public sealed class VersionCounterProvider : IMongoTimestampProvider
{
    private static readonly BsonDocument VersionFilter         = new("_id", "v");
    private static readonly ProjectionDefinition<BsonDocument> SeqProjection =
        Builders<BsonDocument>.Projection.Include("seq").Exclude("_id");

    private readonly IMongoCollection<BsonDocument> _collection;

    public VersionCounterProvider(IMongoClient client, string database = "_mongodelta", string collection = "version")
    {
        _collection = client.GetDatabase(database).GetCollection<BsonDocument>(collection);
    }

    public async Task<string> GetTimestampAsync(CancellationToken cancellationToken = default)
    {
        var doc = await _collection
            .Find(VersionFilter)
            .Project(SeqProjection)
            .FirstOrDefaultAsync(cancellationToken);

        if (doc is null) return "0";

        return doc["seq"].AsInt64.ToString();
    }
}
