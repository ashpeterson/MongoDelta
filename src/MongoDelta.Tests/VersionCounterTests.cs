using EphemeralMongo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace MongoDelta.Tests;

/// <summary>
/// Integration tests for standalone mode (no replica set).
/// Spins up a plain mongod via EphemeralMongo — no $clusterTime available.
/// </summary>
public class VersionCounterTests : IAsyncLifetime
{
    private IMongoRunner _runner = null!;
    private IMongoClient _client = null!;

    public async Task InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync(new MongoRunnerOptions
        {
            UseSingleNodeReplicaSet = false,
        });
        _client = new MongoClient(_runner.ConnectionString);
    }

    public Task DisposeAsync()
    {
        _runner.Dispose();
        return Task.CompletedTask;
    }

    // ── VersionCounterProvider ─────────────────────────────────────────────────

    [Fact]
    public async Task GetTimestamp_NoDocument_ReturnsZero()
    {
        var provider = new VersionCounterProvider(_client);
        Assert.Equal("0", await provider.GetTimestampAsync());
    }

    [Fact]
    public async Task GetTimestamp_AfterOneIncrement_ReturnsOne()
    {
        var store    = new MongoVersionStore(_client);
        var provider = new VersionCounterProvider(_client);

        await store.IncrementAsync();

        Assert.Equal("1", await provider.GetTimestampAsync());
    }

    [Fact]
    public async Task GetTimestamp_AfterFiveIncrements_ReturnsFive()
    {
        var store    = new MongoVersionStore(_client);
        var provider = new VersionCounterProvider(_client);

        for (int i = 0; i < 5; i++)
            await store.IncrementAsync();

        Assert.Equal("5", await provider.GetTimestampAsync());
    }

    [Fact]
    public async Task GetTimestamp_StableWithNoIncrements()
    {
        var store    = new MongoVersionStore(_client);
        var provider = new VersionCounterProvider(_client);

        await store.IncrementAsync();

        var ts1 = await provider.GetTimestampAsync();
        var ts2 = await provider.GetTimestampAsync();

        Assert.Equal(ts1, ts2);
    }

    [Fact]
    public async Task GetTimestamp_ChangesAfterIncrement()
    {
        var store    = new MongoVersionStore(_client);
        var provider = new VersionCounterProvider(_client);

        var before = await provider.GetTimestampAsync();
        await store.IncrementAsync();
        var after = await provider.GetTimestampAsync();

        Assert.NotEqual(before, after);
    }

    // ── MongoVersionStore ──────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentIncrements_AllAppliedAtomically()
    {
        var store    = new MongoVersionStore(_client);
        var provider = new VersionCounterProvider(_client);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.IncrementAsync()));

        Assert.Equal("20", await provider.GetTimestampAsync());
    }

    [Fact]
    public async Task CustomNamespace_WorksIndependentlyOfDefault()
    {
        var customStore    = new MongoVersionStore(_client, "myapp", "delta_ver");
        var customProvider = new VersionCounterProvider(_client, "myapp", "delta_ver");
        var defaultProvider = new VersionCounterProvider(_client);

        await customStore.IncrementAsync();

        Assert.Equal("1", await customProvider.GetTimestampAsync());
        Assert.Equal("0", await defaultProvider.GetTimestampAsync());
    }

    // ── Auto-detection via UseMongoDelta ──────────────────────────────────────

    [Fact]
    public async Task AutoDetect_Standalone_ReturnsNonNullStore()
    {
        using var host = BuildHost(_client, out var store);
        Assert.NotNull(store);
    }

    [Fact]
    public async Task AutoDetect_Standalone_ETagUsesVersionCounter()
    {
        using var host = BuildHost(_client, out _);

        // Before any increment the version counter is "0"
        var response = await host.GetTestClient().GetAsync("/");
        var etag = response.Headers.ETag?.ToString();

        Assert.NotNull(etag);
        // ETag format: "{assemblyTicks}-{counter}" — counter part is numeric (no dot)
        var counterPart = etag!.Trim('"').Split('-').Last();
        Assert.True(long.TryParse(counterPart, out _),
            $"Expected numeric counter in ETag '{etag}', got '{counterPart}'");
    }

    [Fact]
    public async Task AutoDetect_Standalone_304AfterIncrementMatches()
    {
        using var host = BuildHost(_client, out var store);
        var client = host.GetTestClient();

        // First request — get ETag
        var r1 = await client.GetAsync("/");
        var etag = r1.Headers.ETag!.ToString();

        // Increment — data changed
        await store!.IncrementAsync();

        // Second request with old ETag — must be 200 (data changed)
        client.DefaultRequestHeaders.Add("If-None-Match", etag);
        var r2 = await client.GetAsync("/");
        Assert.Equal(200, (int)r2.StatusCode);

        // Third request with new ETag — must be 304
        var newEtag = r2.Headers.ETag!.ToString();
        client.DefaultRequestHeaders.Remove("If-None-Match");
        client.DefaultRequestHeaders.Add("If-None-Match", newEtag);
        var r3 = await client.GetAsync("/");
        Assert.Equal(304, (int)r3.StatusCode);
    }

    [Fact]
    public async Task AutoDetect_ReplicaSet_StoreIsNull()
    {
        using var rsRunner = await MongoRunner.RunAsync(new MongoRunnerOptions
        {
            UseSingleNodeReplicaSet = true,
        });
        var rsClient = new MongoClient(rsRunner.ConnectionString);

        using var host = BuildHost(rsClient, out var store);
        Assert.Null(store);
    }

    [Fact]
    public async Task AutoDetect_ReplicaSet_ETagUsesClusterTime()
    {
        using var rsRunner = await MongoRunner.RunAsync(new MongoRunnerOptions
        {
            UseSingleNodeReplicaSet = true,
        });
        var rsClient = new MongoClient(rsRunner.ConnectionString);

        using var host = BuildHost(rsClient, out _);
        var response = await host.GetTestClient().GetAsync("/");
        var etag = response.Headers.ETag?.ToString();

        Assert.NotNull(etag);
        // ETag format: "{assemblyTicks}-{seconds}.{ordinal}" — clusterTime has a dot
        var tsPart = etag!.Trim('"').Split('-')[^1];
        Assert.Contains('.', tsPart);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static IHost BuildHost(IMongoClient mongoClient, out IMongoVersionStore? store)
    {
        IMongoVersionStore? capturedStore = null;

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.Configure(app =>
                {
                    var (_, s) = app.UseMongoDelta(mongoClient);
                    capturedStore = s;
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        return ctx.Response.WriteAsync("ok");
                    });
                });
            })
            .Start();

        store = capturedStore;
        return host;
    }
}
