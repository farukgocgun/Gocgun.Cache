using StackExchange.Redis;

namespace Gocgun.Cache;

internal static class RedisPrefixDelete
{
    public static async Task DeleteAsync(
        IConnectionMultiplexer multiplexer,
        string pattern,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var database = multiplexer.GetDatabase();
        foreach (var server in multiplexer.GetServers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!server.IsConnected || server.IsReplica)
            {
                continue;
            }

            // KeysAsync issues SCAN with MATCH. The KEYS command is not used.
            await foreach (var key in server.KeysAsync(database: -1, pattern: pattern, pageSize: pageSize, cursor: 0, pageOffset: 0, flags: CommandFlags.None).WithCancellation(cancellationToken))
            {
                await database.KeyDeleteAsync(key).ConfigureAwait(false);
            }
        }
    }
}
