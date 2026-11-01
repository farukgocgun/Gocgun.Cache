using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using StackExchange.Redis;

namespace Gocgun.Cache.Tests;

public sealed class GocgunCacheUnitTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public async Task GetAsync_L1Hit_DoesNotReadRedis()
    {
        var fake = new FakeRedis();
        var payload = new byte[] { 1, 2, 3 };
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());
        await cache.SetAsync("item", payload, Hour, CancellationToken.None);
        fake.Database.ClearReceivedCalls();

        var got = await cache.GetAsync("item", CancellationToken.None);

        Assert.Equal(payload, got);
        await fake.Database.DidNotReceive().StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetAsync_L2Hit_FillsL1()
    {
        var fake = new FakeRedis();
        var payload = new byte[] { 9, 8 };
        fake.Database.StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(new RedisValueWithExpiry(payload, TimeSpan.FromMinutes(2)));
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());

        var first = await cache.GetAsync("item", CancellationToken.None);
        fake.Database.ClearReceivedCalls();
        var second = await cache.GetAsync("item", CancellationToken.None);

        Assert.Equal(payload, first);
        Assert.Equal(payload, second);
        await fake.Database.DidNotReceive().StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetAsync_StoresL2AndPublishesInvalidation()
    {
        var fake = new FakeRedis();
        var payload = new byte[] { 4, 5, 6 };
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());

        await cache.SetAsync("item", payload, Hour, CancellationToken.None);

        await fake.Database.Received().StringSetAsync(
            "item",
            Arg.Is<RedisValue>(value => SameBytes(value, payload)),
            Hour,
            When.Always,
            CommandFlags.None);
        await fake.Subscriber.Received().PublishAsync(
            Arg.Any<RedisChannel>(),
            Arg.Is<RedisValue>(value => value.ToString().Contains("|k|item", StringComparison.Ordinal)),
            CommandFlags.None);
    }

    [Fact]
    public async Task RemoveAsync_DeletesBothLayersAndPublishes()
    {
        var fake = new FakeRedis();
        var payload = new byte[] { 1 };
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());
        await cache.SetAsync("item", payload, Hour, CancellationToken.None);
        fake.Database.ClearReceivedCalls();
        fake.Subscriber.ClearReceivedCalls();

        await cache.RemoveAsync("item", CancellationToken.None);
        fake.Database.StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(new RedisValueWithExpiry(RedisValue.Null, null));
        var got = await cache.GetAsync("item", CancellationToken.None);

        Assert.Null(got);
        await fake.Database.Received().KeyDeleteAsync("item", CommandFlags.None);
        await fake.Subscriber.Received().PublishAsync(
            Arg.Any<RedisChannel>(),
            Arg.Is<RedisValue>(value => value.ToString().Contains("|k|item", StringComparison.Ordinal)),
            CommandFlags.None);
    }

    [Fact]
    public async Task GetAsync_EmptyKey_ThrowsBeforeRedis()
    {
        var fake = new FakeRedis();
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetAsync(string.Empty, CancellationToken.None));
        await fake.Database.DidNotReceive().StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetAsync_NullValue_Throws()
    {
        var fake = new FakeRedis();
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());

        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.SetAsync("item", null!, Hour, CancellationToken.None));
        _ = fake.Database.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetAsync_ZeroTtl_ThrowsBeforeRedis()
    {
        var fake = new FakeRedis();
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.SetAsync("item", [1], TimeSpan.Zero, CancellationToken.None));
        _ = fake.Database.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetAsync_NegativeTtl_Throws()
    {
        var fake = new FakeRedis();
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.SetAsync("item", [1], TimeSpan.FromSeconds(-1), CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_LargeValue_CopiesEveryByte()
    {
        var fake = new FakeRedis();
        var payload = new byte[1024 * 1024];
        payload[0] = 2;
        payload[^1] = 9;
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());

        await cache.SetAsync("big", payload, Hour, CancellationToken.None);
        payload[0] = 0;
        var got = await cache.GetAsync("big", CancellationToken.None);

        Assert.NotNull(got);
        Assert.Equal(1024 * 1024, got.Length);
        Assert.Equal(2, got[0]);
        Assert.Equal(9, got[^1]);
    }

    [Fact]
    public async Task GetAsync_Cancelled_Throws()
    {
        var fake = new FakeRedis();
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.GetAsync("item", source.Token));
    }

    [Fact]
    public async Task RemoveByPrefixAsync_UsesScanAndDoesNotDeleteSibling()
    {
        var fake = new FakeRedis();
        fake.Primary.KeysAsync(Arg.Any<int>(), Arg.Any<RedisValue>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CommandFlags>())
            .Returns(_ => FakeRedis.Keys("user:1:post"));
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());
        await cache.SetAsync("user:1:post", [1], Hour, CancellationToken.None);
        await cache.SetAsync("user:10:post", [2], Hour, CancellationToken.None);
        fake.Database.ClearReceivedCalls();

        await cache.RemoveByPrefixAsync("user:1:", CancellationToken.None);
        var sibling = await cache.GetAsync("user:10:post", CancellationToken.None);

        Assert.Equal(new byte[] { 2 }, sibling);
        await fake.Database.Received().KeyDeleteAsync("user:1:post", CommandFlags.None);
        _ = fake.Database.DidNotReceive().KeyDeleteAsync("user:10:post", CommandFlags.None);
        _ = fake.Primary.Received().KeysAsync(-1, "user:1:*", 250, 0, 0, CommandFlags.None);
        _ = fake.Replica.DidNotReceive().KeysAsync(
            Arg.Any<int>(),
            Arg.Any<RedisValue>(),
            Arg.Any<int>(),
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task RemoveByPrefixAsync_GlobPrefix_ThrowsBeforeScan()
    {
        var fake = new FakeRedis();
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => cache.RemoveByPrefixAsync("*", CancellationToken.None));
        _ = fake.Primary.DidNotReceive().KeysAsync(
            Arg.Any<int>(),
            Arg.Any<RedisValue>(),
            Arg.Any<int>(),
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task HandleNotice_ForeignKey_DropsOnlyThatL1Entry()
    {
        var fake = new FakeRedis();
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());
        await cache.StartAsync(CancellationToken.None);
        await cache.SetAsync("kept", [1], Hour, CancellationToken.None);
        await cache.SetAsync("dropped", [2], Hour, CancellationToken.None);
        fake.Database.ClearReceivedCalls();
        var notice = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"1|{Guid.NewGuid():N}|k|dropped");

        fake.Notice!.Invoke(default, notice);
        fake.Database.StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(new RedisValueWithExpiry(new byte[] { 7 }, Hour));
        var dropped = await cache.GetAsync("dropped", CancellationToken.None);
        fake.Database.ClearReceivedCalls();
        var kept = await cache.GetAsync("kept", CancellationToken.None);

        Assert.Equal(new byte[] { 7 }, dropped);
        Assert.Equal(new byte[] { 1 }, kept);
        await fake.Database.DidNotReceive().StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await fake.Database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task HandleNotice_OwnOriginOrGarbage_KeepsL1()
    {
        var fake = new FakeRedis();
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());
        await cache.StartAsync(CancellationToken.None);
        await cache.SetAsync("item", [4], Hour, CancellationToken.None);
        var own = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"1|{cache.InstanceId:N}|k|item");

        fake.Notice!.Invoke(default, "not-a-notice");
        fake.Notice.Invoke(default, own);
        fake.Database.ClearReceivedCalls();
        var got = await cache.GetAsync("item", CancellationToken.None);

        Assert.Equal(new byte[] { 4 }, got);
        await fake.Database.DidNotReceive().StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public void AddGocgunCache_RegistersCacheAndListener()
    {
        var services = new ServiceCollection();

        services.AddGocgunCache(options => options.ConnectionString = "127.0.0.1:1");

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IGocgunCache));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddGocgunCache_NullArguments_Throw()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddGocgunCache(null!));
        Assert.Throws<ArgumentNullException>(() => GocgunCacheServiceCollectionExtensions.AddGocgunCache(null!, _ => { }));
    }

    [Fact]
    public async Task GetAsync_NoticeDuringRedisRead_DoesNotPinStaleValueInL1()
    {
        var fake = new FakeRedis();
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());
        await cache.StartAsync(CancellationToken.None);
        var notice = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"1|{Guid.NewGuid():N}|k|item");
        fake.Database.StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(_ =>
        {
            fake.Notice!.Invoke(default, notice);
            return new RedisValueWithExpiry(new byte[] { 1 }, Hour);
        });

        var first = await cache.GetAsync("item", CancellationToken.None);
        await cache.GetAsync("item", CancellationToken.None);

        Assert.Equal(new byte[] { 1 }, first);
        await fake.Database.Received(2).StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetAsync_L1LifetimeIsCappedByMaxEntryLifetime()
    {
        var fake = new FakeRedis();
        fake.Database.StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(new RedisValueWithExpiry(new byte[] { 1 }, Hour));
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions { L1MaxEntryLifetime = TimeSpan.FromMilliseconds(50) });

        await cache.GetAsync("item", CancellationToken.None);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        await cache.GetAsync("item", CancellationToken.None);

        await fake.Database.Received(2).StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task ConnectionRestored_ClearsL1()
    {
        var fake = new FakeRedis();
        await using var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());
        await cache.StartAsync(CancellationToken.None);
        await cache.SetAsync("item", [1], Hour, CancellationToken.None);

        fake.Multiplexer.ConnectionRestored += Raise.Event<EventHandler<ConnectionFailedEventArgs>>(fake.Multiplexer, default(ConnectionFailedEventArgs)!);
        fake.Database.ClearReceivedCalls();
        await cache.GetAsync("item", CancellationToken.None);

        await fake.Database.Received(1).StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesAsyncAndSurvivesRedisFailure()
    {
        var fake = new FakeRedis();
        fake.Subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromException(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down")));
        var cache = new GocgunCache(fake.Multiplexer, new GocgunCacheOptions());
        await cache.StartAsync(CancellationToken.None);

        await cache.DisposeAsync();

        await fake.Subscriber.Received(1).UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    private static bool SameBytes(RedisValue value, byte[] payload)
    {
        var bytes = (byte[]?)value;
        return bytes is not null && bytes.SequenceEqual(payload);
    }
}
