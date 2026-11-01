using Microsoft.Extensions.Hosting;

namespace Gocgun.Cache;

internal sealed class GocgunCacheHostedService(GocgunCache cache) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => cache.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => cache.StopAsync(cancellationToken);
}
