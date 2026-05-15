using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDelta;

/// <summary>
/// Atomically increments a single counter document used by <see cref="VersionCounterProvider"/>
/// to signal that application data has changed.
/// </summary>
public sealed class MongoVersionStore : IMongoVersionStore
{
    private static readonly BsonDocument VersionFilter     = new("_id", "v");
    private static readonly UpdateDefinition<BsonDocument> IncrementSeq =
        Builders<BsonDocument>.Update.Inc("seq", 1L);
    private static readonly FindOneAndUpdateOptions<BsonDocument> UpsertOptions = new()
    {
        IsUpsert = true,
    };

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoVersionStore(IMongoClient client, string database = "_mongodelta", string collection = "version")
    {
        _collection = client.GetDatabase(database).GetCollection<BsonDocument>(collection);
    }

    public async Task IncrementAsync(CancellationToken cancellationToken = default)
    {
        await _collection.FindOneAndUpdateAsync(
            VersionFilter,
            IncrementSeq,
            UpsertOptions,
            cancellationToken);
    }
}
