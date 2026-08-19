using AdaPanel.Core.Dtos.Cache;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: Redis/Memcached SUNUCU GENELİ hizmetlerdir — bütün siteler aynı örneği paylaşır.
// Durdurma, o önbelleğe yaslanan her siteyi aynı anda etkiler (oturumlar düşer, sayfa üretimi
// yavaşlar) ve dışarıdan "site yavaşladı" olarak görünür. Kayıt olmadan bu kesintinin bir panel
// eylemi mi yoksa çöken bir süreç mi olduğu ayırt edilemez.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/cache")]
public sealed class CacheController(
    ICacheService cacheService,
    IAuditService auditService) : ControllerBase
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
        await CalismaZamaniniKaydetAsync("redis", "started", runtime);
        return Ok(ApiResponse<CacheRuntimeDto>.Ok(runtime, "cache.started"));
    }

    [HttpPost("redis/stop")]
    public async Task<ActionResult<ApiResponse<CacheRuntimeDto>>> StopRedisAsync(CancellationToken cancellationToken)
    {
        var runtime = await cacheService.StopRedisAsync(cancellationToken);
        await CalismaZamaniniKaydetAsync("redis", "stopped", runtime);
        return Ok(ApiResponse<CacheRuntimeDto>.Ok(runtime, "cache.stopped"));
    }

    [HttpPost("memcached/start")]
    public async Task<ActionResult<ApiResponse<CacheRuntimeDto>>> StartMemcachedAsync(CancellationToken cancellationToken)
    {
        var runtime = await cacheService.StartMemcachedAsync(cancellationToken);
        await CalismaZamaniniKaydetAsync("memcached", "started", runtime);
        return Ok(ApiResponse<CacheRuntimeDto>.Ok(runtime, "cache.started"));
    }

    [HttpPost("memcached/stop")]
    public async Task<ActionResult<ApiResponse<CacheRuntimeDto>>> StopMemcachedAsync(CancellationToken cancellationToken)
    {
        var runtime = await cacheService.StopMemcachedAsync(cancellationToken);
        await CalismaZamaniniKaydetAsync("memcached", "stopped", runtime);
        return Ok(ApiResponse<CacheRuntimeDto>.Ok(runtime, "cache.stopped"));
    }

    /// <summary>
    /// İSTENEN eylemi değil, işlem sonrası ÖLÇÜLEN durumu da yazar.
    /// </summary>
    /// <remarks>
    /// "Durdur" isteği ile hizmetin gerçekten durmuş olması aynı şey değildir: servis kurulu
    /// olmayabilir, durdurma sessizce başarısız olabilir. Yalnız isteği kaydeden bir iz, sonradan
    /// yanlış bir hikâye anlatır. Bu yüzden dönen çalışma zamanı durumu da kayda geçer.
    /// </remarks>
    private Task CalismaZamaniniKaydetAsync(string motor, string eylem, CacheRuntimeDto runtime) =>
        auditService.AuditAsync(
            HttpContext,
            $"cache.{motor}.{eylem}",
            $"cache:{motor}",
            $"Cache engine '{motor}' {eylem} (installed: {runtime.IsInstalled}, service: {runtime.ServiceName}, "
            + $"status: {runtime.Status}, port: {runtime.DefaultPort}).");
}
