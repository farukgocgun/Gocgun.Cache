namespace Gocgun.Cache.Tests;

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
