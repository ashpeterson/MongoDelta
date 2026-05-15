namespace MongoDelta;

/// <summary>
/// Provides a monotonically increasing string token representing the current
/// state of the MongoDB deployment, used as the database-timestamp component
/// of a MongoDelta ETag.
/// </summary>
/// <remarks>
/// The built-in implementations are:
/// <list type="bullet">
///   <item><see cref="ClusterTimeProvider"/> — replica sets and Atlas (<c>$clusterTime</c>)</item>
///   <item><see cref="VersionCounterProvider"/> — standalone mongod (version counter collection)</item>
/// </list>
/// Implement this interface to supply a custom timestamp source.
/// </remarks>
public interface IMongoTimestampProvider
{
    /// <summary>
    /// Returns the current timestamp token. Must be non-decreasing: two successive
    /// calls with no intervening writes must return the same value; a call after a
    /// write must return a value greater than or equal to the previous call.
    /// </summary>
    Task<string> GetTimestampAsync(CancellationToken cancellationToken = default);
}
