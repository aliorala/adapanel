using AdaPanel.Core.Dtos.Cache;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/cache")]
public sealed class CacheController(ICacheService cacheService) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<CacheStatusDto>>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await cacheService.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<CacheStatusDto>.Ok(status));
    }

    [HttpPost("redis/start")]
    public async Task<ActionResult<ApiResponse<CacheRuntimeDto>>> StartRedisAsync(CancellationToken cancellationToken)
    {
        var runtime = await cacheService.StartRedisAsync(cancellationToken);
        return Ok(ApiResponse<CacheRuntimeDto>.Ok(runtime, "cache.started"));
    }

    [HttpPost("redis/stop")]
    public async Task<ActionResult<ApiResponse<CacheRuntimeDto>>> StopRedisAsync(CancellationToken cancellationToken)
    {
        var runtime = await cacheService.StopRedisAsync(cancellationToken);
        return Ok(ApiResponse<CacheRuntimeDto>.Ok(runtime, "cache.stopped"));
    }

    [HttpPost("memcached/start")]
    public async Task<ActionResult<ApiResponse<CacheRuntimeDto>>> StartMemcachedAsync(CancellationToken cancellationToken)
    {
        var runtime = await cacheService.StartMemcachedAsync(cancellationToken);
        return Ok(ApiResponse<CacheRuntimeDto>.Ok(runtime, "cache.started"));
    }

    [HttpPost("memcached/stop")]
    public async Task<ActionResult<ApiResponse<CacheRuntimeDto>>> StopMemcachedAsync(CancellationToken cancellationToken)
    {
        var runtime = await cacheService.StopMemcachedAsync(cancellationToken);
        return Ok(ApiResponse<CacheRuntimeDto>.Ok(runtime, "cache.stopped"));
    }
}
