using System.Globalization;
using StackExchange.Redis;

namespace Gocgun.Cache.Tests;

[Collection(RedisCollection.Name)]
public sealed class GocgunCacheIntegrationTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly RedisFixture fixture;

    public GocgunCacheIntegrationTests(RedisFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task GetSetRemove_RoundTripsAcrossTwoProcesses()
    {
        var scope = NewScope();
        using var firstConnection = await ConnectAsync(keysDisabled: false);
        using var secondConnection = await ConnectAsync(keysDisabled: false);
        await using var first = Cache(firstConnection, scope);
        await using var second = Cache(secondConnection, scope);
        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);
        var payload = new byte[] { 1, 2, 3, 4 };

        await first.SetAsync("item", payload, Hour, CancellationToken.None);
        var loaded = await second.GetAsync("item", CancellationToken.None);
        await first.RemoveAsync("item", CancellationToken.None);
        await WaitUntil(async () => await second.GetAsync("item", CancellationToken.None) is null);

        Assert.Equal(payload, loaded);
    }

    [Fact]
    public async Task SetAsync_EmptyKeyOrZeroTtl_Throws()
    {
        var scope = NewScope();
        using var connection = await ConnectAsync(keysDisabled: false);
        await using var cache = Cache(connection, scope);

        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetAsync(string.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.SetAsync("item", [1], TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_LargeValue_RoundTrips()
    {
        var scope = NewScope();
        using var connection = await ConnectAsync(keysDisabled: false);
        await using var cache = Cache(connection, scope);
        var payload = new byte[1024 * 1024];
        payload[0] = 3;
        payload[^1] = 8;

        await cache.SetAsync("big", payload, Hour, CancellationToken.None);
        var got = await cache.GetAsync("big", CancellationToken.None);

        Assert.NotNull(got);
        Assert.Equal(payload, got);
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WithKeysDisabled_LeavesSiblingKeys()
    {
        var scope = NewScope();
        using var connection = await ConnectAsync(keysDisabled: true);
        await using var cache = Cache(connection, scope);
        await cache.SetAsync("user:1:post", [1], Hour, CancellationToken.None);
        await cache.SetAsync("user:10:post", [2], Hour, CancellationToken.None);
        await cache.SetAsync("user:1-post", [3], Hour, CancellationToken.None);

        await cache.RemoveByPrefixAsync("user:1:", CancellationToken.None);

        Assert.Null(await cache.GetAsync("user:1:post", CancellationToken.None));
        Assert.Equal(new byte[] { 2 }, await cache.GetAsync("user:10:post", CancellationToken.None));
        Assert.Equal(new byte[] { 3 }, await cache.GetAsync("user:1-post", CancellationToken.None));
    }

    [Fact]
    public async Task Publish_ForgedNotice_DropsOnlyTargetL1AndKeepsL2()
    {
        var scope = NewScope();
        using var connection = await ConnectAsync(keysDisabled: false);
        using var publisher = await ConnectAsync(keysDisabled: false);
        await using var cache = Cache(connection, scope);
        await cache.StartAsync(CancellationToken.None);
        var original = new byte[] { 1, 1 };
        var sibling = new byte[] { 2, 2 };
        var replaced = new byte[] { 9, 9 };
        await cache.SetAsync("target", original, Hour, CancellationToken.None);
        await cache.SetAsync("other", sibling, Hour, CancellationToken.None);
        var database = publisher.GetDatabase();
        var subscriber = publisher.GetSubscriber();
        var channel = Channel(scope);

        await subscriber.PublishAsync(channel, "forged");
        Assert.Equal(original, await cache.GetAsync("target", CancellationToken.None));

        var forged = string.Create(CultureInfo.InvariantCulture, $"1|{Guid.NewGuid():N}|k|{scope.KeyPrefix}target");
        await subscriber.PublishAsync(channel, forged);
        await database.StringSetAsync(scope.KeyPrefix + "target", replaced, Hour);
        await WaitUntil(async () =>
        {
            var got = await cache.GetAsync("target", CancellationToken.None);
            return got is not null && got.SequenceEqual(replaced);
        });
        await database.StringSetAsync(scope.KeyPrefix + "other", replaced, Hour);

        Assert.Equal(sibling, await cache.GetAsync("other", CancellationToken.None));
    }

    private static Scope NewScope()
    {
        var id = Guid.NewGuid().ToString("N");
        return new Scope(id + ":", "gocgun.cache.invalidate." + id);
    }

    private static GocgunCache Cache(IConnectionMultiplexer multiplexer, Scope scope)
    {
        return new GocgunCache(
            multiplexer,
            new GocgunCacheOptions
            {
                KeyPrefix = scope.KeyPrefix,
                InvalidationChannel = scope.ChannelName,
            });
    }

    private static RedisChannel Channel(Scope scope) => new(scope.ChannelName, RedisChannel.PatternMode.Literal);

    private static async Task WaitUntil(Func<Task<bool>> probe)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (await probe().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for cache invalidation.");
    }

    private Task<ConnectionMultiplexer> ConnectAsync(bool keysDisabled)
    {
        var options = ConfigurationOptions.Parse(fixture.ConnectionString);
        options.AbortOnConnectFail = false;
        if (keysDisabled)
        {
            options.CommandMap = CommandMap.Create(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KEYS" }, available: false);
        }

        return ConnectionMultiplexer.ConnectAsync(options);
    }

    private readonly record struct Scope(string KeyPrefix, string ChannelName);
}
