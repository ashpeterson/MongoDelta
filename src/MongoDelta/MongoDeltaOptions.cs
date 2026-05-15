using Microsoft.AspNetCore.Http;

namespace MongoDelta;

/// <summary>Configuration options for <see cref="MongoDeltaMiddleware"/>.</summary>
public sealed class MongoDeltaOptions
{
    /// <summary>
    /// Database used to run the <c>hello</c> command for replica-set detection.
    /// Does not scope the ETag — <c>$clusterTime</c> is always cluster-wide.
    /// Defaults to <c>"admin"</c>.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// Optional per-request string appended to the ETag, allowing caching to be
    /// scoped per user, tenant, or any other request dimension.
    /// Return <c>null</c> to omit the suffix for that request.
    /// <para>
    /// When set, pair with <see cref="VaryByHeaders"/> so that shared caches
    /// (CDNs, reverse proxies) do not serve one user's cached response to another.
    /// </para>
    /// </summary>
    public Func<HttpContext, string?>? Suffix { get; set; }

    /// <summary>
    /// When this predicate returns <c>true</c> the middleware is bypassed entirely
    /// for that request. Useful for health checks, metrics endpoints, or any route
    /// that must never return 304.
    /// </summary>
    public Func<HttpContext, bool>? OnSkip { get; set; }

    /// <summary>
    /// Header names to include in a <c>Vary</c> response header on 2xx responses.
    /// Required when <see cref="Suffix"/> produces user- or tenant-scoped ETags so
    /// that shared caches key their entries correctly.
    /// <example><code>
    /// options.VaryByHeaders = ["Authorization"];
    /// </code></example>
    /// </summary>
    public string[]? VaryByHeaders { get; set; }

    /// <summary>
    /// Overrides the assembly write-time component of the ETag.
    /// Use in containerised or multi-instance deployments where different pods may
    /// have different file timestamps for the same build.
    /// Typically set from an environment variable or build-injected constant:
    /// <code>
    /// options.DeploymentVersion = Environment.GetEnvironmentVariable("DEPLOY_SHA") ?? "dev";
    /// </code>
    /// When <c>null</c> (default) the entry assembly's <c>LastWriteTimeUtc</c> ticks are used.
    /// </summary>
    public string? DeploymentVersion { get; set; }

    // ── Standalone (version counter) options ──────────────────────────────────

    /// <summary>
    /// Database used to store the version counter document when running on standalone
    /// mongod. Defaults to <c>"_mongodelta"</c>. Must already exist or the application
    /// user must have permission to create databases.
    /// </summary>
    public string VersionCounterDatabase { get; set; } = "_mongodelta";

    /// <summary>
    /// Collection name for the version counter document within
    /// <see cref="VersionCounterDatabase"/>. Defaults to <c>"version"</c>.
    /// </summary>
    public string VersionCounterCollection { get; set; } = "version";
}
