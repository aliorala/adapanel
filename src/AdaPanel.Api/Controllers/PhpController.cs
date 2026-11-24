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

// Müşteri (Plesk gibi) KENDİ sitesinin PHP sürümünü ve limitlerini yönetir; bayi kendi müşterilerinin
// sitelerinde yönetir (EnsureSiteAsync her site ucunda). PHP runtime KURULUMU/kaldırılması, redis
// uzantısı ve GENEL php.ini server-level → admin-only (eylem düzeyinde "Administrator"; sınıf ve eylem
// rolleri VE ile birleştiği için sınıfa Customer eklemek bu uçları müşteriye AÇMAZ).
// Askı: SubscriptionSuspensionFilter küresel olduğundan askıdaki müşterinin yazma uçları 403 döner.
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
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
        if (!User.IsInRole("Administrator"))
        {
            // Sürüm SEÇMEK için kimlik/ad/sürüm yeter; kurulum dizini ve exe yolu sunucu düzenini sızdırır.
            versions = versions.Select(item => new PhpVersionDto
            {
                ComponentId = item.ComponentId,
                DisplayName = item.DisplayName,
                Version = item.Version,
                Status = item.Status,
                IsFastCgiRegistered = item.IsFastCgiRegistered,
                InstalledAtUtc = item.InstalledAtUtc
            }).ToArray();
        }

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
        var disariyaAcilan = ScrubForNonAdministrator(assignment);

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

        var notice = assignment.ErrorLogging?.Status != "verified" ? "php.logging.unverified" : assignment.HandlerOrderNoticeKey;
        return Ok(ApiResponse<SitePhpAssignmentDto>.Ok(
            disariyaAcilan,
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
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(PanelRateLimits.PhpSettings)]
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

        // MÜŞTERİ "sınırsız" yazamaz: memory_limit=-1 ve max_execution_time=0 paylaşımlı sunucuda
        // komşu kiracıları aç bırakır; üst sınırı bayi/yönetici koyar. Normal sayısal limitler serbest.
        if (User.IsInRole("Customer")
            && (request.MemoryLimit.StartsWith('-') || request.MaxExecutionTime == 0 || MemoryBytes(request.MemoryLimit) > CustomerMemoryCeilingBytes))
        {
            throw new AppException("php.limits_staff_only", 403, 403);
        }

        // Yönetici dışı çağrıda sunucu geneli yan etkiler (tüm php-cgi'yi öldürme, tüm FastCGI zaman
        // aşımını yükseltme) servis katmanında KAPALI; izolasyon yenilemesi yalnız o sitenin havuzunu kapsar.
        request.TenantScope = !User.IsInRole("Administrator");

        var settings = await phpService.UpdateSitePhpSettingsAsync(siteName, request, cancellationToken);
        await auditService.WriteForSubscriptionAsync(
            "php.site.settings.updated",
            $"php:{siteName}",
            $"PHP settings updated for site '{siteName}'.",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<SitePhpSettingsDto>.Ok(settings,
            settings.ErrorLogging?.Status == "verified" ? "php.settings.success" : "php.logging.unverified"));
    }

    [HttpPost("/api/v1/sites/{siteName}/php/logging/verify")]
    public async Task<ActionResult<ApiResponse<PhpErrorLoggingDto>>> VerifySiteLoggingAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await phpService.VerifySiteErrorLoggingAsync(siteName, cancellationToken);

        // Dogrulama IIS/PHP hata gunlugu yolunu siteye uygulayip geri okur; sahibi bilen iz birakir.
        await auditService.WriteForSubscriptionAsync(
            "php.site.logging.verified",
            $"php:{siteName}",
            $"PHP error logging for site '{siteName}' was verified (status: {result.Status}).",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return Ok(ApiResponse<PhpErrorLoggingDto>.Ok(result, result.Status == "verified" ? "php.logging.verified" : "php.logging.unverified"));
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

    /// <summary>Müşterinin yazabileceği memory_limit üst sınırı (2 GB). Yönetici/bayi bunun üstünü verebilir.</summary>
    internal const long CustomerMemoryCeilingBytes = 2L * 1024 * 1024 * 1024;

    internal static long MemoryBytes(string value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (text.Length == 0 || !long.TryParse(text.TrimEnd('K', 'M', 'G'), out var number) || number < 0)
        {
            return long.MaxValue;
        }

        return text[^1] switch
        {
            'K' => number * 1024,
            'M' => number * 1024 * 1024,
            'G' => number * 1024 * 1024 * 1024,
            _ => number
        };
    }

    /// <summary>Yönetici dışına exe yolu verilmez (sunucu düzeni; müşteriye faydası yok).</summary>
    private SitePhpAssignmentDto ScrubForNonAdministrator(SitePhpAssignmentDto assignment)
    {
        if (User.IsInRole("Administrator"))
        {
            return assignment;
        }

        return new SitePhpAssignmentDto
        {
            ErrorLogging = assignment.ErrorLogging,
            SiteName = assignment.SiteName,
            ComponentId = assignment.ComponentId,
            DisplayName = assignment.DisplayName,
            Version = assignment.Version,
            HandlerName = assignment.HandlerName,
            IsFastCgiRegistered = assignment.IsFastCgiRegistered,
            HandlerOrder = assignment.HandlerOrder
        };
    }
}
