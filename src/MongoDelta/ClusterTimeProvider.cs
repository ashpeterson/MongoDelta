using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDelta;

/// <summary>
/// Reads <c>$clusterTime</c> from the MongoDB <c>hello</c> command and returns it
/// as a <c>"{seconds}.{ordinal}"</c> string.
/// </summary>
/// <remarks>
/// Requires a replica set or sharded cluster. Not available on standalone mongod —
/// use <see cref="VersionCounterProvider"/> in that case, or let
/// <see cref="MongoDeltaExtensions.UseMongoDelta(Microsoft.AspNetCore.Builder.IApplicationBuilder, MongoDB.Driver.IMongoClient, System.Action{MongoDeltaOptions}?)"/>
/// auto-detect the correct provider.
/// <para>
/// On sharded clusters the <c>hello</c> response from <c>mongos</c> already
/// reflects the highest <c>$clusterTime</c> seen across all shards, so no special
/// handling is required.
/// </para>
/// </remarks>
public sealed class ClusterTimeProvider : IMongoTimestampProvider
{
    private static readonly BsonDocument HelloCommand = new("hello", 1);

    private readonly IMongoDatabase _db;

    public ClusterTimeProvider(IMongoClient client, string? database = null)
    {
        // hello is a server-level command; the database context doesn't affect the result.
        _db = client.GetDatabase(database ?? "admin");
    }

    public async Task<string> GetTimestampAsync(CancellationToken cancellationToken = default)
    {
        var result = await _db.RunCommandAsync<BsonDocument>(HelloCommand, cancellationToken: cancellationToken);
        var ts = result["$clusterTime"]["clusterTime"].AsBsonTimestamp;
        return $"{ts.Timestamp}.{ts.Increment}";
    }
}
