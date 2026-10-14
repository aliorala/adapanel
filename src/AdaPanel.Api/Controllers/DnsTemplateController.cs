using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Sunucu-geneli DNS şablonu (Plesk "DNS template" gibi): yeni site bölgeleri bu şablonla seed edilir. Admin-only.
//
// Şablon ayrıca SUNUCU-GENELİ İKİNCİL (slave) DNS SUNUCULARINI tutar (Plesk'te bu, "Slave DNS
// Manager" eklentisi + "Transfer Restrictions Template" ikilisidir). Buradaki her IP, panelin
// birincil olduğu HER bölgede AXFR izni alır — yani bu uç, sunucudaki tüm alan adlarının zone
// içeriğini kimin kopyalayabileceğini belirler. Bu yüzden denetim kaydı ZORUNLU.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/dns/template")]
public sealed class DnsTemplateController(IDnsService dnsService, IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<DnsTemplateDto>>> GetAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<DnsTemplateDto>.Ok(await dnsService.GetTemplateAsync(cancellationToken)));

    [HttpPut]
    public async Task<ActionResult<ApiResponse<DnsTemplateDto>>> UpdateAsync(
        [FromBody] UpdateDnsTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var template = await dnsService.UpdateTemplateAsync(request, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "dns.template_saved",
            "dns:template",
            // Cumle degil OLCUM: eylem etiketi ("DNS template saved") zaten cevriliyor,
            // ayrinti ise `AuditLogsView`'da HAM basiliyor -- Turkce cumle burada Ingilizce
            // panelde de gorunurdu.
            $"records={template.Records.Count}; " +
            $"secondaryServers={(template.SecondaryDnsServers.Count == 0 ? "(none)" : string.Join(", ", template.SecondaryDnsServers))}");

        return Ok(template.MappingResults.Any(result => !result.Succeeded)
            ? ApiResponse<DnsTemplateDto>.Fail("dns.sync_failed", data: template)
            : ApiResponse<DnsTemplateDto>.Ok(template, "dns.template_saved"));
    }
}

/// <summary>
/// DNS sunucusunun dışarıya yanıt verebilirliği (53 portunun fiili dinleyicileri) + tek tık onarım.
/// Sunucu-geneli operasyonel işlem → admin-only. Sunucu Sağlığı ekranı bu uçları kullanır.
/// </summary>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/dns/server")]
public sealed class DnsServerController(IDnsService dnsService, IAuditService auditService) : ControllerBase
{
    [HttpGet("health")]
    public async Task<ActionResult<ApiResponse<DnsServerHealthDto>>> GetHealthAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<DnsServerHealthDto>.Ok(await dnsService.GetServerHealthAsync(cancellationToken)));

    [HttpPost("repair")]
    public async Task<ActionResult<ApiResponse<DnsServerHealthDto>>> RepairAsync(
        [FromBody] RepairDnsServerRequest? request,
        CancellationToken cancellationToken)
    {
        var applyDelayedAutoStart = request?.ApplyDelayedAutoStart ?? true;
        var health = await dnsService.RepairServerAsync(applyDelayedAutoStart, cancellationToken);

        // DNS servisini yeniden başlatmak SUNUCUDAKİ TÜM alan adlarını birkaç saniyeliğine
        // çözülemez yapar ve başlangıç türünü değiştirmek kalıcı bir sistem ayarıdır. İz bırakmayan
        // bir "kısa süreli toptan kesinti", sonradan hiçbir yerde aranamaz.
        await auditService.AuditAsync(
            HttpContext,
            "dns.server_repaired",
            "dns:server",
            $"delayedAutoStart={applyDelayedAutoStart}; " +
            $"listening={string.Join(", ", health.ListeningAddresses)}; needsRepair={health.NeedsRepair}");

        return Ok(ApiResponse<DnsServerHealthDto>.Ok(health, "dns.server_repaired"));
    }
}

public sealed class RepairDnsServerRequest
{
    /// <summary>Servisi gecikmeli otomatik başlangıca da al (reboot yarış durumuna karşı). Varsayılan: açık.</summary>
    public bool ApplyDelayedAutoStart { get; init; } = true;
}
