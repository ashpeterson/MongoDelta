namespace MongoDelta;

/// <summary>
/// Signals that application data has changed on a standalone mongod instance.
/// Call <see cref="IncrementAsync"/> after every write operation so that
/// MongoDelta can issue a fresh ETag on the next request.
/// </summary>
/// <remarks>
/// Not needed when running on a replica set or Atlas — $clusterTime advances
/// automatically on every write without any application involvement.
/// </remarks>
public interface IMongoVersionStore
{
    Task IncrementAsync(CancellationToken cancellationToken = default);
}
