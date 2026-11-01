using Testcontainers.Redis;

namespace Gocgun.Cache.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer container = new RedisBuilder("redis:8").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
