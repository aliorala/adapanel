using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Licensing;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Lisans / aktivasyon yönetimi.
/// </summary>
/// <remarks>
/// <para>
/// YALNIZ yönetici. Bayi ve müşteri lisans durumunu görmez: lisans sunucunun sahibiyle bizim
/// aramızdaki iş ilişkisidir, kiracının bilgisi değildir.
/// </para>
/// <para>
/// Lisans anahtarı hiçbir yanıtta DÖNMEZ; yalnız son dört karakteri gösterilir. Denetim kaydına da
/// yalnız son dört karakter yazılır.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/license")]
public sealed class LicenseController(
    ILicenseService licenseService,
    IAuditService auditService) : ControllerBase
{
    /// <summary>
    /// Güncel lisans durumu. Doğrulama aralığı dolduysa arka planda yeniden doğrulamayı dener;
    /// sunucuya ulaşılamazsa çağrı yine de BAŞARILI döner (çevrimdışı tolerans).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<LicenseStatusDto>>> GetAsync(CancellationToken cancellationToken)
    {
        var status = await licenseService.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<LicenseStatusDto>.Ok(status));
    }

    /// <summary>Lisans anahtarını panele tanıtır (şifreli saklanır) ve hemen doğrulamayı dener.</summary>
    [HttpPost("activate")]
    public async Task<ActionResult<ApiResponse<LicenseStatusDto>>> ActivateAsync(
        [FromBody] LicenseActivateRequest request,
        CancellationToken cancellationToken)
    {
        var status = await licenseService.ActivateAsync(request.LicenseKey, cancellationToken);

        await auditService.WriteAsync(
            "license.activated",
            "license",
            // Anahtarın KENDİSİ değil yalnız son dört karakteri: denetim kaydı bir sır deposu değildir.
            $"License '****{status.KeySuffix}' activated. state={status.State}, sku={status.Sku}",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<LicenseStatusDto>.Ok(status, "license.activated"));
    }

    /// <summary>Doğrulama aralığını beklemeden sunucuya sorar.</summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<LicenseStatusDto>>> RefreshAsync(CancellationToken cancellationToken)
    {
        var status = await licenseService.RefreshAsync(cancellationToken);

        // İZ YOKTU. Doğrulama SONUCU kalıcıdır: sunucu "geçersiz" derse panel o andan itibaren yeni
        // kaynak açmayı reddeder. Yani bu uç, sonucu ölçüldüğü an durumu değiştiren bir işlemdir ve
        // "panel neden bir anda lisanssız oldu" sorusunun tek başlangıç noktası budur.
        //
        // KİRACI BAĞI YOK: lisans kuruluma aittir, hiçbir aboneliğe değil.
        await auditService.WriteAsync(
            "license.refreshed",
            "license",
            $"License re-validated on demand. state={status.State}, sku={status.Sku}",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<LicenseStatusDto>.Ok(status, "license.refreshed"));
    }

    /// <summary>
    /// Saklı anahtarı siler (makine değiştirme / iade). Mevcut siteler, posta kutuları ve
    /// veritabanları bundan ETKİLENMEZ; yalnız yeni kaynak açma kapısı kapanır.
    /// </summary>
    [HttpDelete]
    public async Task<ActionResult<ApiResponse<LicenseStatusDto>>> DeactivateAsync(CancellationToken cancellationToken)
    {
        var status = await licenseService.DeactivateAsync(cancellationToken);

        await auditService.WriteAsync(
            "license.deactivated",
            "license",
            "License key removed from this installation.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<LicenseStatusDto>.Ok(status, "license.deactivated"));
    }
}
