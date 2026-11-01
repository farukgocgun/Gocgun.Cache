namespace Gocgun.Cache;

/// <summary>
/// Byte cache. L1 is in-process memory. L2 is Redis. Callers pass already serialized bytes.
/// </summary>
public interface IGocgunCache
{
    /// <summary>
    /// Gets the stored bytes, or null when the key is missing.
    /// </summary>
    /// <param name="key">Logical key. An application prefix from options is applied.</param>
    /// <param name="cancellationToken">Token that cancels the Redis read.</param>
    /// <returns>A copy of the stored bytes, or null.</returns>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Stores bytes in L2 and L1 and tells other processes to drop their L1 entry.
    /// </summary>
    /// <param name="key">Logical key.</param>
    /// <param name="value">Bytes to store. The cache copies them.</param>
    /// <param name="absoluteExpiration">Lifetime of the L2 key and the L1 entry.</param>
    /// <param name="cancellationToken">Token that cancels the Redis write.</param>
    /// <returns>A task that completes when both layers and the invalidation publish have finished.</returns>
    Task SetAsync(string key, byte[] value, TimeSpan absoluteExpiration, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one key from L1 and L2 and tells other processes to drop their L1 entry.
    /// </summary>
    /// <param name="key">Logical key.</param>
    /// <param name="cancellationToken">Token that cancels the Redis delete.</param>
    /// <returns>A task that completes when the delete and the invalidation publish have finished.</returns>
    Task RemoveAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every L2 key under a prefix by SCAN and tells other processes to drop matching L1 entries.
    /// </summary>
    /// <param name="prefix">Logical prefix. Must not contain SCAN glob characters.</param>
    /// <param name="cancellationToken">Token that cancels the scan.</param>
    /// <returns>A task that completes when matching keys are deleted and the invalidation publish has finished.</returns>
    Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken);
}
