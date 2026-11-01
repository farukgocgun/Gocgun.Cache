using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Gocgun.Cache;

/// <summary>
/// L1 <see cref="MemoryCache"/> and L2 Redis. Remove publishes a notice so other processes drop L1.
/// </summary>
public sealed class GocgunCache : IGocgunCache, IAsyncDisposable
{
    private readonly MemoryCache memoryCache;
    private readonly IConnectionMultiplexer multiplexer;
    private readonly string keyPrefix;
    private readonly string invalidationChannel;
    private readonly TimeSpan l1FallbackEntryLifetime;
    private readonly TimeSpan l1MaxEntryLifetime;
    private readonly int scanPageSize;
    private readonly ConcurrentDictionary<string, byte[]> tracked = new(StringComparer.Ordinal);
    private readonly Guid instanceId = Guid.NewGuid();
    private ISubscriber? subscriber;
    private Action<RedisChannel, RedisValue>? onNotice;
    private long generation;
    private int listening;
    private int disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="GocgunCache"/> class.
    /// </summary>
    /// <param name="multiplexer">Redis connection. The caller owns its lifetime.</param>
    /// <param name="options">Cache settings.</param>
    public GocgunCache(IConnectionMultiplexer multiplexer, GocgunCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentNullException.ThrowIfNull(options);
        if (options.L1EntryLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "L1 entry limit must be positive.");
        }

        if (options.ScanPageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SCAN page size must be positive.");
        }

        if (options.L1FallbackEntryLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "L1 fallback lifetime must be positive.");
        }

        if (options.L1MaxEntryLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "L1 max lifetime must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.InvalidationChannel))
        {
            throw new ArgumentException("Invalidation channel is required.", nameof(options));
        }

        var prefix = options.KeyPrefix ?? string.Empty;
        CacheKeyRules.RejectGlob(prefix, nameof(options));
        this.multiplexer = multiplexer;
        keyPrefix = prefix;
        invalidationChannel = options.InvalidationChannel;
        l1FallbackEntryLifetime = options.L1FallbackEntryLifetime;
        l1MaxEntryLifetime = options.L1MaxEntryLifetime;
        scanPageSize = options.ScanPageSize;
        memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.L1EntryLimit });
    }

    /// <summary>
    /// Gets the origin stamped on invalidation notices from this instance.
    /// </summary>
    public Guid InstanceId => instanceId;

    /// <summary>
    /// Subscribes to L1 invalidation notices.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels subscription startup.</param>
    /// <returns>A task that completes when the subscription is active.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        if (Interlocked.CompareExchange(ref listening, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        return StartCoreAsync();
    }

    /// <summary>
    /// Drops the L1 invalidation subscription. The L1 store stays alive.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels unsubscription.</param>
    /// <returns>A task that completes when the subscription is gone.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref listening, 0) != 1 || subscriber is null || onNotice is null)
        {
            return Task.CompletedTask;
        }

        multiplexer.ConnectionRestored -= HandleConnectionRestored;
        return subscriber.UnsubscribeAsync(Channel(), onNotice, CommandFlags.None);
    }

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullKey = CacheKeyRules.FullKey(keyPrefix, key);
        var local = ReadL1(fullKey);
        if (local is not null)
        {
            return Task.FromResult<byte[]?>(local);
        }

        return GetFromRedisAsync(fullKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetAsync(string key, byte[] value, TimeSpan absoluteExpiration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(value);
        if (absoluteExpiration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteExpiration), "Expiration must be positive.");
        }

        var fullKey = CacheKeyRules.FullKey(keyPrefix, key);
        return SetCoreAsync(fullKey, value, absoluteExpiration, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullKey = CacheKeyRules.FullKey(keyPrefix, key);
        return RemoveCoreAsync(fullKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPrefix = CacheKeyRules.FullPrefix(keyPrefix, prefix);
        return RemovePrefixCoreAsync(fullPrefix, cancellationToken);
    }

    /// <summary>
    /// Unsubscribes and releases L1.
    /// </summary>
    /// <returns>A task that completes after cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref listening, 0) == 1 && subscriber is not null && onNotice is not null)
        {
            multiplexer.ConnectionRestored -= HandleConnectionRestored;
            try
            {
                await subscriber.UnsubscribeAsync(Channel(), onNotice, CommandFlags.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or ObjectDisposedException or TimeoutException)
            {
                // Redis may already be gone at shutdown. The subscription dies with the connection.
            }
        }

        memoryCache.Dispose();
    }

    private static byte[] CopyOf(byte[] value)
    {
        var copy = new byte[value.Length];
        value.AsSpan().CopyTo(copy);
        return copy;
    }

    private async Task StartCoreAsync()
    {
        try
        {
            subscriber = multiplexer.GetSubscriber();
            onNotice = HandleNotice;
            await subscriber.SubscribeAsync(Channel(), onNotice, CommandFlags.None).ConfigureAwait(false);
            multiplexer.ConnectionRestored += HandleConnectionRestored;
        }
        catch
        {
            Interlocked.Exchange(ref listening, 0);
            throw;
        }
    }

    private async Task<byte[]?> GetFromRedisAsync(string fullKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = multiplexer.GetDatabase();

        // An invalidation that lands while the read is in flight bumps the generation; then the value is not cached.
        var seen = Volatile.Read(ref generation);
        var result = await database.StringGetWithExpiryAsync(fullKey, CommandFlags.None).ConfigureAwait(false);
        var stored = (byte[]?)result.Value;
        if (stored is null)
        {
            return null;
        }

        var owned = CopyOf(stored);
        var lifetime = result.Expiry is { } remaining && remaining > TimeSpan.Zero ? remaining : l1FallbackEntryLifetime;
        if (Volatile.Read(ref generation) == seen)
        {
            PutL1(fullKey, owned, lifetime);
            if (Volatile.Read(ref generation) != seen)
            {
                DropKey(fullKey);
            }
        }

        return CopyOf(owned);
    }

    private async Task SetCoreAsync(string fullKey, byte[] value, TimeSpan absoluteExpiration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = CopyOf(value);
        var database = multiplexer.GetDatabase();
        await database.StringSetAsync(fullKey, owned, absoluteExpiration, When.Always, CommandFlags.None).ConfigureAwait(false);
        PutL1(fullKey, owned, absoluteExpiration);
        await PublishAsync(new InvalidationNotice(instanceId, false, fullKey), cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveCoreAsync(string fullKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = multiplexer.GetDatabase();
        await database.KeyDeleteAsync(fullKey, CommandFlags.None).ConfigureAwait(false);
        DropKey(fullKey);
        await PublishAsync(new InvalidationNotice(instanceId, false, fullKey), cancellationToken).ConfigureAwait(false);
    }

    private async Task RemovePrefixCoreAsync(string fullPrefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RedisPrefixDelete.DeleteAsync(multiplexer, fullPrefix + "*", scanPageSize, cancellationToken).ConfigureAwait(false);
        DropPrefix(fullPrefix);
        await PublishAsync(new InvalidationNotice(instanceId, true, fullPrefix), cancellationToken).ConfigureAwait(false);
    }

    private Task<long> PublishAsync(InvalidationNotice notice, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var publisher = subscriber ?? multiplexer.GetSubscriber();
        return publisher.PublishAsync(Channel(), notice.Format(), CommandFlags.None);
    }

    private void HandleNotice(RedisChannel channel, RedisValue message)
    {
        if (!InvalidationNotice.TryParse(message.ToString(), out var notice) || notice.Origin == instanceId)
        {
            return;
        }

        if (notice.Prefix)
        {
            DropPrefix(notice.Key);
            return;
        }

        DropKey(notice.Key);
    }

    private byte[]? ReadL1(string fullKey)
    {
        if (memoryCache.TryGetValue(fullKey, out byte[]? stored) && stored is not null)
        {
            return CopyOf(stored);
        }

        return null;
    }

    private void PutL1(string fullKey, byte[] value, TimeSpan lifetime)
    {
        var entry = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = lifetime < l1MaxEntryLifetime ? lifetime : l1MaxEntryLifetime,
            Size = 1,
        };
        entry.RegisterPostEvictionCallback(
            static (key, evicted, _, state) =>
            {
                if (key is string name && evicted is byte[] bytes && state is ConcurrentDictionary<string, byte[]> map)
                {
                    map.TryRemove(new KeyValuePair<string, byte[]>(name, bytes));
                }
            },
            tracked);
        tracked[fullKey] = value;
        memoryCache.Set(fullKey, value, entry);
    }

    private void HandleConnectionRestored(object? sender, ConnectionFailedEventArgs args)
    {
        // Notices published while the connection was down are gone; start L1 from scratch.
        Interlocked.Increment(ref generation);
        memoryCache.Clear();
        tracked.Clear();
    }

    private void DropKey(string fullKey)
    {
        Interlocked.Increment(ref generation);
        memoryCache.Remove(fullKey);
        tracked.TryRemove(fullKey, out _);
    }

    private void DropPrefix(string fullPrefix)
    {
        Interlocked.Increment(ref generation);
        var names = tracked.Keys.Where(name => name.StartsWith(fullPrefix, StringComparison.Ordinal)).ToArray();

        foreach (var name in names)
        {
            DropKey(name);
        }
    }

    private RedisChannel Channel() => new(invalidationChannel, RedisChannel.PatternMode.Literal);
}
