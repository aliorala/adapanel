using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/system")]
public sealed class SystemController(ISystemMonitoringService systemMonitoringService) : ControllerBase
{
    /// <summary>
    /// Sunucunun anlık durumu. YÖNETİCİ tam görünüm alır; diğer roller daraltılmış görünüm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uç <c>[Authorize]</c> ile her role açık KALIYOR ama gövde daraltılıyor. Sebep ölçüldü:
    /// müşteri panosu bu ucu girişte ve 15 saniyede bir çağırıyor, üstelik <c>await</c> zincirinin
    /// ortasında — 403 dönmek panonun tamamını yükleyemez hâle getirirdi. Sızıntı yetkiyle değil
    /// gövdeyle kapatılıyor.
    /// </para>
    /// <para>
    /// Daraltmanın hangi alanı neden çıkardığı <see cref="SystemOverviewScope"/>'ta yazılı.
    /// </para>
    /// </remarks>
    [HttpGet("overview")]
    public async Task<ActionResult<ApiResponse<SystemOverviewDto>>> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var overview = await systemMonitoringService.GetOverviewAsync(cancellationToken);
        return Ok(ApiResponse<SystemOverviewDto>.Ok(
            SystemOverviewScope.ForRole(overview, User.IsInRole("Administrator"))));
    }

    /// <summary>
    /// Sunucunun TÜM IP adresleri. Yalnız yönetici.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Burada gövde daraltmak anlamsız: ucun tek içeriği zaten adres listesidir. Uç
    /// <c>[Authorize]</c> ile her role açıktı; müşteri sunucunun bütün IP'lerini (yönetim
    /// arabirimleri, iç ağ adresleri dahil) okuyabiliyordu.
    /// </para>
    /// <para>
    /// <b>Arayüz kırılmıyor — ÖLÇÜLDÜ:</b> ucun iki çağıranı da hatayı yutuyor
    /// (<c>DashboardOverviewView</c> <c>try/catch</c> ile, <c>SiteDetailView</c>
    /// <c>.catch(() =&gt; [])</c> ile) ve listeyi boş bırakıyor. Bayi de dışarıda: adresi bağlama
    /// için seçen akış (<c>SiteDetailView</c>) o düğmeyi zaten yalnız yöneticiye gösteriyor.
    /// </para>
    /// </remarks>
    [HttpGet("ip-addresses")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ServerIpAddressDto>>>> GetIpAddressesAsync(CancellationToken cancellationToken)
    {
        var addresses = await systemMonitoringService.GetServerIpAddressesAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<ServerIpAddressDto>>.Ok(addresses));
    }

    /// <summary>
    /// Kalıcı izleme geçmişi (CPU/RAM/ağ örnekleri) → izleme ekranı trend grafikleri.
    /// </summary>
    /// <remarks>
    /// Anlık görünümle AYNI bilgi, üstelik zaman serisi hâlinde — daha da açıklayıcı. Anlık
    /// görünüm daraltılıp bu açık bırakılsaydı daraltma hiçbir işe yaramazdı. Çağıran yalnız
    /// izleme ekranıdır ve o ekran zaten yönetici ekranıdır.
    /// </remarks>
    [HttpGet("history")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SystemHistorySampleDto>>>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        var history = await systemMonitoringService.GetHistoryAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<SystemHistorySampleDto>>.Ok(history));
    }
}
