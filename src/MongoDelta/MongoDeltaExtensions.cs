using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDelta;

/// <summary>Extension methods for registering MongoDelta middleware.</summary>
public static class MongoDeltaExtensions
{
    /// <summary>
    /// Adds the MongoDelta HTTP 304 caching middleware.
    /// Auto-detects the deployment mode at startup:
    /// replica set / Atlas → uses $clusterTime (zero application changes needed);
    /// standalone mongod → uses a version counter (requires calling
    /// <see cref="IMongoVersionStore.IncrementAsync"/> after every write).
    /// </summary>
    /// <returns>
    /// On replica sets: null (no store needed).
    /// On standalone: the <see cref="IMongoVersionStore"/> to call after writes.
    /// </returns>
    public static (IApplicationBuilder App, IMongoVersionStore? Store) UseMongoDelta(
        this IApplicationBuilder app,
        IMongoClient mongoClient,
        Action<MongoDeltaOptions>? configure = null)
    {
        var options = new MongoDeltaOptions();
        configure?.Invoke(options);

        var (provider, store) = DetectAndCreate(app, mongoClient, options);
        return (app.UseMiddleware<MongoDeltaMiddleware>(provider, options), store);
    }

    /// <summary>Advanced overload for supplying a custom <see cref="IMongoTimestampProvider"/>.</summary>
    public static IApplicationBuilder UseMongoDelta(
        this IApplicationBuilder app,
        IMongoTimestampProvider provider,
        Action<MongoDeltaOptions>? configure = null)
    {
        var options = new MongoDeltaOptions();
        configure?.Invoke(options);
        return app.UseMiddleware<MongoDeltaMiddleware>(provider, options);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static readonly BsonDocument HelloCommand = new("hello", 1);

    private static (IMongoTimestampProvider Provider, IMongoVersionStore? Store)
        DetectAndCreate(IApplicationBuilder app, IMongoClient client, MongoDeltaOptions options)
    {
        var logger = app.ApplicationServices
            .GetService(typeof(ILogger<MongoDeltaMiddleware>)) as ILogger<MongoDeltaMiddleware>;

        try
        {
            var result = client
                .GetDatabase(options.Database ?? "admin")
                .RunCommand<BsonDocument>(HelloCommand);

            if (result.Contains("$clusterTime"))
            {
                logger?.LogInformation(
                    "MongoDelta: replica set detected — using $clusterTime provider");
                return (new ClusterTimeProvider(client, options.Database), null);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "MongoDelta: hello command failed — falling back to version counter");
        }

        logger?.LogInformation(
            "MongoDelta: standalone mongod detected — using version counter provider. " +
            "Call IMongoVersionStore.IncrementAsync() after every write.");

        var store    = new MongoVersionStore(client, options.VersionCounterDatabase, options.VersionCounterCollection);
        var provider = new VersionCounterProvider(client, options.VersionCounterDatabase, options.VersionCounterCollection);
        return (provider, store);
    }
}
