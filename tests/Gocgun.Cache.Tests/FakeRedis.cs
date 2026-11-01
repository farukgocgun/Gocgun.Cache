using NSubstitute;
using StackExchange.Redis;

namespace Gocgun.Cache.Tests;

internal sealed class FakeRedis
{
    public FakeRedis()
    {
        Multiplexer = Substitute.For<IConnectionMultiplexer>();
        Database = Substitute.For<IDatabase>();
        Subscriber = Substitute.For<ISubscriber>();
        Primary = Substitute.For<IServer>();
        Replica = Substitute.For<IServer>();
        Primary.IsConnected.Returns(true);
        Primary.IsReplica.Returns(false);
        Replica.IsConnected.Returns(true);
        Replica.IsReplica.Returns(true);
        Multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Database);
        Multiplexer.GetSubscriber(Arg.Any<object?>()).Returns(Subscriber);
        Multiplexer.GetServers().Returns([Replica, Primary]);
        Database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        Database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        Database.StringGetWithExpiryAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(new RedisValueWithExpiry(RedisValue.Null, null));
        Subscriber.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns(0L);
        Subscriber.SubscribeAsync(
            Arg.Any<RedisChannel>(),
            Arg.Do<Action<RedisChannel, RedisValue>>(handler => Notice = handler),
            Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        Subscriber.UnsubscribeAsync(
            Arg.Any<RedisChannel>(),
            Arg.Any<Action<RedisChannel, RedisValue>>(),
            Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        Primary.KeysAsync(Arg.Any<int>(), Arg.Any<RedisValue>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CommandFlags>())
            .Returns(_ => NoKeys());
    }

    public IConnectionMultiplexer Multiplexer { get; }

    public IDatabase Database { get; }

    public ISubscriber Subscriber { get; }

    public IServer Primary { get; }

    public IServer Replica { get; }

    public Action<RedisChannel, RedisValue>? Notice { get; private set; }

    public static async IAsyncEnumerable<RedisKey> Keys(params string[] names)
    {
        foreach (var name in names)
        {
            yield return name;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<RedisKey> NoKeys()
    {
        await Task.Yield();
        yield break;
    }
}
