using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace MongoDelta.Tests;

public class MiddlewareTests
{
    private const string FixedTimestamp = "1778000000.1";
    private static readonly string ExpectedETag =
        MongoDeltaMiddleware.BuildETag(FixedTimestamp, null);

    private static IHost BuildHost(
        IMongoTimestampProvider provider,
        Action<MongoDeltaOptions>? configure = null,
        int handlerStatusCode = 200)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s => s.AddRouting());
                web.Configure(app =>
                {
                    app.UseMongoDelta(provider, configure);
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = handlerStatusCode;
                        return ctx.Response.WriteAsync("body");
                    });
                });
            })
            .Start();
    }

    private static IMongoTimestampProvider FixedProvider()
    {
        var p = Substitute.For<IMongoTimestampProvider>();
        p.GetTimestampAsync(Arg.Any<CancellationToken>()).Returns(FixedTimestamp);
        return p;
    }

    // ── ETag format ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildETag_NoSuffix_WrappedInQuotes()
    {
        var etag = MongoDeltaMiddleware.BuildETag("1234.5", null);
        Assert.StartsWith("\"", etag);
        Assert.EndsWith("\"", etag);
        Assert.Contains("1234.5", etag);
    }

    [Fact]
    public void BuildETag_WithSuffix_SuffixAppended()
    {
        var etag = MongoDeltaMiddleware.BuildETag("1234.5", "alice");
        Assert.Contains("1234.5-alice", etag);
    }

    [Fact]
    public void BuildETag_EmptySuffix_TreatedAsNoSuffix()
    {
        var withEmpty = MongoDeltaMiddleware.BuildETag("1234.5", "");
        var withNull  = MongoDeltaMiddleware.BuildETag("1234.5", null);
        Assert.Equal(withNull, withEmpty);
    }

    // ── 304 short-circuit ─────────────────────────────────────────────────────

    [Fact]
    public async Task Get_MatchingIfNoneMatch_Returns304()
    {
        using var host = BuildHost(FixedProvider());
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.Add("If-None-Match", ExpectedETag);
        var response = await client.GetAsync("/");

        Assert.Equal(304, (int)response.StatusCode);
    }

    [Fact]
    public async Task Get_MatchingIfNoneMatch_ProviderCalledOnce()
    {
        var provider = FixedProvider();
        using var host = BuildHost(provider);
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.Add("If-None-Match", ExpectedETag);
        await client.GetAsync("/");

        await provider.Received(1).GetTimestampAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_MatchingIfNoneMatch_NoResponseBody()
    {
        using var host = BuildHost(FixedProvider());
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.Add("If-None-Match", ExpectedETag);
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Empty(body);
    }

    // ── 200 with ETag + Cache-Control ─────────────────────────────────────────

    [Fact]
    public async Task Get_NoIfNoneMatch_Returns200WithETag()
    {
        using var host = BuildHost(FixedProvider());
        var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(ExpectedETag, response.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Get_NoIfNoneMatch_SetsCacheControlNoCache()
    {
        using var host = BuildHost(FixedProvider());
        var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Get_MismatchedIfNoneMatch_Returns200WithFreshETag()
    {
        using var host = BuildHost(FixedProvider());
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.Add("If-None-Match", "\"stale-etag\"");
        var response = await client.GetAsync("/");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(ExpectedETag, response.Headers.ETag?.ToString());
    }

    // ── Non-GET methods skipped ───────────────────────────────────────────────

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task NonGetMethod_MiddlewareSkipped_NoETag(string method)
    {
        using var host = BuildHost(FixedProvider());
        var request = new HttpRequestMessage(new HttpMethod(method), "/");
        var response = await host.GetTestClient().SendAsync(request);

        Assert.False(response.Headers.Contains("ETag"));
    }

    // ── HEAD ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Head_MatchingIfNoneMatch_Returns304()
    {
        using var host = BuildHost(FixedProvider());
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.Add("If-None-Match", ExpectedETag);
        var request  = new HttpRequestMessage(HttpMethod.Head, "/");
        var response = await client.SendAsync(request);

        Assert.Equal(304, (int)response.StatusCode);
    }

    [Fact]
    public async Task Head_NoIfNoneMatch_Returns200WithETag()
    {
        using var host = BuildHost(FixedProvider());
        var request  = new HttpRequestMessage(HttpMethod.Head, "/");
        var response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(ExpectedETag, response.Headers.ETag?.ToString());
    }

    // ── OnSkip predicate ──────────────────────────────────────────────────────

    [Fact]
    public async Task OnSkip_ReturnsTrue_MiddlewareSkipped()
    {
        using var host = BuildHost(FixedProvider(), opt =>
            opt.OnSkip = _ => true);
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.Add("If-None-Match", ExpectedETag);
        var response = await client.GetAsync("/");

        // Must be 200 (handler ran) not 304 (middleware short-circuited).
        Assert.Equal(200, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("ETag"));
    }

    [Fact]
    public async Task OnSkip_ReturnsFalse_MiddlewareActive()
    {
        using var host = BuildHost(FixedProvider(), opt =>
            opt.OnSkip = _ => false);
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.Add("If-None-Match", ExpectedETag);
        var response = await client.GetAsync("/");

        Assert.Equal(304, (int)response.StatusCode);
    }

    // ── Suffix ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Suffix_IncludedInETag()
    {
        using var host = BuildHost(FixedProvider(), opt =>
            opt.Suffix = _ => "tenant-42");
        var response = await host.GetTestClient().GetAsync("/");

        var etag = response.Headers.ETag?.ToString();
        Assert.Contains("tenant-42", etag);
    }

    [Fact]
    public async Task Suffix_MatchingIfNoneMatch_Returns304()
    {
        using var host = BuildHost(FixedProvider(), opt =>
            opt.Suffix = _ => "user-7");
        var client = host.GetTestClient();

        var expectedWithSuffix = MongoDeltaMiddleware.BuildETag(FixedTimestamp, "user-7");
        client.DefaultRequestHeaders.Add("If-None-Match", expectedWithSuffix);
        var response = await client.GetAsync("/");

        Assert.Equal(304, (int)response.StatusCode);
    }

    // ── Non-2xx handler response ──────────────────────────────────────────────

    [Fact]
    public async Task Handler404_NoETagSet()
    {
        using var host = BuildHost(FixedProvider(), handlerStatusCode: 404);
        var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal(404, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("ETag"));
    }

    [Fact]
    public async Task Handler500_NoETagSet()
    {
        using var host = BuildHost(FixedProvider(), handlerStatusCode: 500);
        var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal(500, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("ETag"));
    }

    // ── If-None-Match: wildcard and multi-value ───────────────────────────────

    [Fact]
    public async Task IfNoneMatch_WildcardStar_Returns304()
    {
        using var host = BuildHost(FixedProvider());
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.Add("If-None-Match", "*");
        var response = await client.GetAsync("/");

        Assert.Equal(304, (int)response.StatusCode);
    }

    [Fact]
    public async Task IfNoneMatch_CommaSeparatedList_MatchesCurrentETag()
    {
        using var host = BuildHost(FixedProvider());
        var client = host.GetTestClient();

        // Send two ETags; the second is the current one
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "If-None-Match", $"\"stale-etag\", {ExpectedETag}");
        var response = await client.GetAsync("/");

        Assert.Equal(304, (int)response.StatusCode);
    }

    [Fact]
    public async Task IfNoneMatch_CommaSeparatedList_NoneMatch_Returns200()
    {
        using var host = BuildHost(FixedProvider());
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "If-None-Match", "\"stale-1\", \"stale-2\"");
        var response = await client.GetAsync("/");

        Assert.Equal(200, (int)response.StatusCode);
    }

    // ── DeploymentVersion ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeploymentVersion_OverridesAssemblyTimeInETag()
    {
        using var host = BuildHost(FixedProvider(), opt =>
            opt.DeploymentVersion = "abc123");
        var response = await host.GetTestClient().GetAsync("/");

        var etag = response.Headers.ETag?.ToString();
        Assert.NotNull(etag);
        Assert.StartsWith("\"abc123-", etag);
    }

    [Fact]
    public async Task DeploymentVersion_OldAssemblyETagNotAccepted()
    {
        // Client has an ETag built with the default assembly time.
        // After setting DeploymentVersion the format changes — old ETag should not match.
        using var host = BuildHost(FixedProvider(), opt =>
            opt.DeploymentVersion = "new-deploy");
        var client = host.GetTestClient();

        // Old ETag (built without DeploymentVersion)
        client.DefaultRequestHeaders.Add("If-None-Match", ExpectedETag);
        var response = await client.GetAsync("/");

        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task DeploymentVersion_NewETagMatches304()
    {
        using var host = BuildHost(FixedProvider(), opt =>
            opt.DeploymentVersion = "v2");
        var client = host.GetTestClient();

        // Get the ETag with the new deployment version
        var r1 = await client.GetAsync("/");
        var newEtag = r1.Headers.ETag!.ToString();

        client.DefaultRequestHeaders.Add("If-None-Match", newEtag);
        var r2 = await client.GetAsync("/");

        Assert.Equal(304, (int)r2.StatusCode);
    }

    // ── VaryByHeaders ─────────────────────────────────────────────────────────

    [Fact]
    public async Task VaryByHeaders_Set_EmittedOnOkResponse()
    {
        using var host = BuildHost(FixedProvider(), opt =>
        {
            opt.Suffix        = _ => "user";
            opt.VaryByHeaders = ["Authorization"];
        });
        var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("Authorization", response.Headers.Vary.ToString());
    }

    [Fact]
    public async Task VaryByHeaders_MultipleHeaders_JoinedWithComma()
    {
        using var host = BuildHost(FixedProvider(), opt =>
            opt.VaryByHeaders = ["Authorization", "X-Tenant-Id"]);
        var response = await host.GetTestClient().GetAsync("/");

        Assert.Contains("Authorization", response.Headers.Vary.ToString());
        Assert.Contains("X-Tenant-Id", response.Headers.Vary.ToString());
    }

    [Fact]
    public async Task VaryByHeaders_NotSet_NoVaryHeaderOnResponse()
    {
        using var host = BuildHost(FixedProvider());
        var response = await host.GetTestClient().GetAsync("/");

        Assert.False(response.Headers.Contains("Vary"));
    }

    [Fact]
    public async Task VaryByHeaders_NotEmittedOn304()
    {
        using var host = BuildHost(FixedProvider(), opt =>
            opt.VaryByHeaders = ["Authorization"]);
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.Add("If-None-Match", ExpectedETag);
        var response = await client.GetAsync("/");

        Assert.Equal(304, (int)response.StatusCode);
        // 304 body is empty; Vary should not appear either (OnStarting never fires)
        Assert.False(response.Headers.Contains("Vary"));
    }

    [Fact]
    public async Task VaryByHeaders_NotEmittedOnErrorResponse()
    {
        using var host = BuildHost(FixedProvider(),
            configure: opt => opt.VaryByHeaders = ["Authorization"],
            handlerStatusCode: 500);
        var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal(500, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("Vary"));
    }

    // ── Provider exception propagation ────────────────────────────────────────

    [Fact]
    public async Task ProviderThrows_ExceptionPropagates()
    {
        var provider = Substitute.For<IMongoTimestampProvider>();
        provider.GetTimestampAsync(Arg.Any<CancellationToken>())
                .Returns<string>(_ => throw new InvalidOperationException("provider failed"));

        using var host = BuildHost(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.GetTestClient().GetAsync("/"));
    }
}
