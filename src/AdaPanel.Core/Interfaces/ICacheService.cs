using AdaPanel.Core.Dtos.Cache;

namespace AdaPanel.Core.Interfaces;

public interface ICacheService
{
    Task<CacheStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<CacheRuntimeDto> StartRedisAsync(CancellationToken cancellationToken = default);

    Task<CacheRuntimeDto> StopRedisAsync(CancellationToken cancellationToken = default);

    Task<CacheRuntimeDto> StartMemcachedAsync(CancellationToken cancellationToken = default);

    Task<CacheRuntimeDto> StopMemcachedAsync(CancellationToken cancellationToken = default);
}
