using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gocgun.Cache;

/// <summary>
/// Registers <see cref="IGocgunCache"/>.
/// </summary>
public static class GocgunCacheServiceCollectionExtensions
{
    /// <summary>
    /// Adds the byte cache, its Redis connection, and the L1 invalidation listener.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Options callback. Set the Redis connection string here.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGocgunCache(this IServiceCollection services, Action<GocgunCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<GocgunCacheOptions>().Configure(configure);
        services.TryAddSingleton<IConnectionMultiplexer>(static serviceProvider =>
        {
            var connectionString = serviceProvider.GetRequiredService<IOptions<GocgunCacheOptions>>().Value.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Gocgun.Cache connection string is required.");
            }

            return ConnectionMultiplexer.Connect(connectionString);
        });
        services.TryAddSingleton<GocgunCache>(static serviceProvider => new GocgunCache(
            serviceProvider.GetRequiredService<IConnectionMultiplexer>(),
            serviceProvider.GetRequiredService<IOptions<GocgunCacheOptions>>().Value));
        services.TryAddSingleton<IGocgunCache>(static serviceProvider => serviceProvider.GetRequiredService<GocgunCache>());
        services.AddHostedService<GocgunCacheHostedService>();
        return services;
    }
}
