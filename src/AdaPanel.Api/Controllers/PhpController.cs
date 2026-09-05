using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) site-bazlı PHP ayarlarını yalnız kendi müşterilerinin sitelerinde yönetebilir
// (EnsureSiteAsync). PHP runtime KURULUMU server-level → admin-only.
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/php")]
public sealed class PhpController(
    IPhpService phpService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("versions")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PhpVersionDto>>>> GetInstalledVersionsAsync(CancellationToken cancellationToken)
    {
        var versions = await phpService.GetInstalledVersionsAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<PhpVersionDto>>.Ok(versions));
    }

    // Native phpredis (redis) PHP uzantısını kurulu PHP'ye indirip etkinleştirir (server-level → admin).
    [HttpPost("enable-redis")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<RedisExtensionResultDto>>> EnableRedisAsync(CancellationToken cancellationToken)
    {
        var result = await phpService.EnableRedisExtensionAsync(cancellationToken);
        await auditService.WriteAsync(
            "php.redis.enabled",
            "php",
            $"phpredis: {result.Message} {result.FileName}".Trim(),
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return Ok(ApiResponse<RedisExtensionResultDto>.Ok(result, result.Message == "already" ? "php.redis_already" : "php.redis_enabled"));
    }

    [HttpPost("install")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<ComponentInstallResultDto>>> InstallVersionAsync(
        [FromBody] ComponentInstallRequest request,
        CancellationToken cancellationToken)
    {
        var result = await phpService.InstallVersionAsync(
            request.ComponentId,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        await auditService.WriteAsync(
            "php.install.completed",
            "php",
            $"PHP runtime '{request.ComponentId}' installed and FastCGI registered.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<ComponentInstallResultDto>.Ok(result, result.Message));
    }

    [HttpPost("/api/v1/sites/{siteName}/php")]
    public async Task<ActionResult<ApiResponse<SitePhpAssignmentDto>>> SetSitePhpVersionAsync(
        string siteName,
        [FromBody] SetSitePhpVersionRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var assignment = await phpService.SetSitePhpVersionAsync(siteName, request, cancellationToken);

        // KONU = SİTENİN SAHİBİ. PHP sürümünü değiştirmek sitenin çalışıp çalışmadığını belirler;
        // yönetici bunu kiracının sitesinde yaptığında kiracı, "sitem neden 500 veriyor" sorusunun
        // cevabını kendi denetim ekranında görebilmeli.
        // ÖLÇÜLEN SIRA denetim kaydına girer: "atandı" ile "atandı ama .php 404.3 dönecek"
        // aynı satıra düşmemeli.
        var siraDetay = assignment.HandlerOrder.Unverified
            ? "handler order UNVERIFIED"
            : assignment.HandlerOrder.Missing
                ? "handler MISSING after commit"
                : assignment.HandlerOrder.PrecedesCatchAll
                    ? $"handler precedes catch-all (index {assignment.HandlerOrder.HandlerIndex})"
                    : $"handler SHADOWED by '{assignment.HandlerOrder.BlockingHandlerName}' -> 404.3";

        await auditService.WriteForSubscriptionAsync(
            "php.site.assignment.completed",
            $"php:{siteName}",
            $"PHP runtime '{request.ComponentId}' assigned to site '{siteName}'. {siraDetay}.",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        var notice = assignment.HandlerOrderNoticeKey;
        return Ok(ApiResponse<SitePhpAssignmentDto>.Ok(
            assignment,
            notice.Length > 0 ? notice : "php.assignment.success"));
    }

    [HttpGet("/api/v1/sites/{siteName}/php/settings")]
    public async Task<ActionResult<ApiResponse<SitePhpSettingsDto>>> GetSitePhpSettingsAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var settings = await phpService.GetSitePhpSettingsAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SitePhpSettingsDto>.Ok(settings));
    }

    [HttpPost("/api/v1/sites/{siteName}/php/settings")]
    public async Task<ActionResult<ApiResponse<SitePhpSettingsDto>>> UpdateSitePhpSettingsAsync(
        string siteName,
        [FromBody] UpdateSitePhpSettingsRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // KİRACI İZOLASYONUNU KAPATMAK SUNUCU DÜZEYİ BİR KARARDIR → yalnız Administrator.
        //
        // Neden: bu uç Reseller'a da açık (bayi kendi müşterisinin sitesinde PHP limitlerini
        // ayarlayabilsin diye). open_basedir'i kapatmak ise o sitenin PHP'sine SUNUCUDAKİ BAŞKA
        // kiracıların köklerini okuma imkânı verir — yani bayi, kendi müşterisinin sitesi üzerinden
        // BAŞKA bayilerin müşterilerini okuyabilirdi. Açmak serbesttir (güvenliği artırır),
        // kapatmak değil. Plesk'te de open_basedir yalnız sunucu yöneticisinin kararıdır.
        if (request.OpenBasedirEnabled is false && !User.IsInRole("Administrator"))
        {
            throw new AppException("php.isolation_admin_only", 403, 403);
        }

        var settings = await phpService.UpdateSitePhpSettingsAsync(siteName, request, cancellationToken);
        await auditService.WriteForSubscriptionAsync(
            "php.site.settings.updated",
            $"php:{siteName}",
            $"PHP settings updated for site '{siteName}'.",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<SitePhpSettingsDto>.Ok(settings, "php.settings.success"));
    }

    // GENEL php.ini ayarları (phpMyAdmin/Adminer dahil tüm PHP araçlarının limitleri). Server-level → admin-only.
    [HttpGet("global-settings")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<SitePhpSettingsDto>>> GetGlobalPhpSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await phpService.GetGlobalPhpSettingsAsync(cancellationToken);
        return Ok(ApiResponse<SitePhpSettingsDto>.Ok(settings));
    }

    [HttpPost("global-settings")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<SitePhpSettingsDto>>> UpdateGlobalPhpSettingsAsync(
        [FromBody] UpdateSitePhpSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await phpService.UpdateGlobalPhpSettingsAsync(request, cancellationToken);
        await auditService.WriteAsync(
            "php.global.settings.updated",
            "php",
            "Global PHP settings (php.ini) were updated.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<SitePhpSettingsDto>.Ok(settings, "php.settings.success"));
    }
}
