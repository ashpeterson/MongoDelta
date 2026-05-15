using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MongoDelta;

/// <summary>
/// ASP.NET Core middleware that returns HTTP 304 Not Modified when the client's
/// cached ETag matches the current MongoDB cluster state, avoiding redundant
/// response body serialisation and database reads.
/// </summary>
/// <remarks>
/// <para>
/// The ETag is composed of three parts:
/// <c>"{deploymentToken}-{dbTimestamp}-{suffix}"</c>
/// </para>
/// <list type="bullet">
///   <item><term>deploymentToken</term>
///     <description>
///       Entry assembly <c>LastWriteTimeUtc</c> ticks, or
///       <see cref="MongoDeltaOptions.DeploymentVersion"/> when set. Ensures
///       a redeploy always invalidates all client caches.
///     </description>
///   </item>
///   <item><term>dbTimestamp</term>
///     <description>
///       Replica set: <c>$clusterTime</c> from the MongoDB <c>hello</c> command
///       (format <c>seconds.ordinal</c>). Standalone: numeric version counter.
///     </description>
///   </item>
///   <item><term>suffix</term>
///     <description>
///       Optional value from <see cref="MongoDeltaOptions.Suffix"/> for
///       per-user / per-tenant cache scoping. Omitted when null.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class MongoDeltaMiddleware
{
    /// <summary>Assembly write time resolved once at startup.</summary>
    internal static readonly long AssemblyWriteTime = ResolveAssemblyWriteTime();

    private readonly RequestDelegate _next;
    private readonly IMongoTimestampProvider _provider;
    private readonly MongoDeltaOptions _options;
    private readonly ILogger<MongoDeltaMiddleware> _logger;

    /// <inheritdoc cref="MongoDeltaMiddleware"/>
    public MongoDeltaMiddleware(
        RequestDelegate next,
        IMongoTimestampProvider provider,
        MongoDeltaOptions options,
        ILogger<MongoDeltaMiddleware> logger)
    {
        _next     = next;
        _provider = provider;
        _options  = options;
        _logger   = logger;
    }

    /// <summary>Middleware entry point invoked by the ASP.NET Core pipeline.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsGetOrHead(context) || _options.OnSkip?.Invoke(context) == true)
        {
            await _next(context);
            return;
        }

        var timestamp       = await _provider.GetTimestampAsync(context.RequestAborted);
        var suffix          = _options.Suffix?.Invoke(context);
        var deploymentToken = _options.DeploymentVersion ?? AssemblyWriteTime.ToString();
        var etag            = BuildETag(deploymentToken, timestamp, suffix);

        if (IfNoneMatchContains(context, etag))
        {
            _logger.LogDebug("304 Not Modified — ETag matched: {ETag}", etag);
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        // OnStarting fires when the response headers are about to be sent, after
        // the handler has set its final status code but before any body bytes go out.
        context.Response.OnStarting(() =>
        {
            if (context.Response.StatusCode is >= 200 and < 300)
            {
                context.Response.Headers.ETag         = etag;
                context.Response.Headers.CacheControl = "no-cache";

                if (_options.VaryByHeaders is { Length: > 0 })
                    context.Response.Headers.Vary = string.Join(", ", _options.VaryByHeaders);

                _logger.LogDebug("200 OK — ETag set: {ETag}", etag);
            }
            return Task.CompletedTask;
        });

        await _next(context);
    }

    // ── ETag helpers ─────────────────────────────────────────────────────────

    /// <summary>Builds a quoted ETag string from its constituent parts.</summary>
    internal static string BuildETag(string deploymentToken, string timestamp, string? suffix) =>
        string.IsNullOrEmpty(suffix)
            ? $"\"{deploymentToken}-{timestamp}\""
            : $"\"{deploymentToken}-{timestamp}-{suffix}\"";

    /// <summary>
    /// Compatibility overload used in unit tests where the deployment token
    /// is always the static <see cref="AssemblyWriteTime"/>.
    /// </summary>
    internal static string BuildETag(string timestamp, string? suffix) =>
        BuildETag(AssemblyWriteTime.ToString(), timestamp, suffix);

    // ── If-None-Match matching ────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the request's <c>If-None-Match</c> header matches the
    /// given ETag. Handles a comma-separated list of ETags and the wildcard <c>*</c>.
    /// </summary>
    private static bool IfNoneMatchContains(HttpContext context, string etag)
    {
        var header = context.Request.Headers.IfNoneMatch.ToString();
        if (string.IsNullOrEmpty(header)) return false;
        if (header == "*") return true;

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, etag, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsGetOrHead(HttpContext context) =>
        HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);

    private static long ResolveAssemblyWriteTime()
    {
        var location = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(location)) return 0;
        var fi = new FileInfo(location);
        return fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0;
    }
}
