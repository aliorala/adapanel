using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Sunucu-geneli DNS şablonu (Plesk "DNS template" gibi): yeni site bölgeleri bu şablonla seed edilir. Admin-only.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/dns/template")]
public sealed class DnsTemplateController(IDnsService dnsService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<DnsTemplateDto>>> GetAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<DnsTemplateDto>.Ok(await dnsService.GetTemplateAsync(cancellationToken)));

    [HttpPut]
    public async Task<ActionResult<ApiResponse<DnsTemplateDto>>> UpdateAsync(
        [FromBody] UpdateDnsTemplateRequest request,
        CancellationToken cancellationToken)
        => Ok(ApiResponse<DnsTemplateDto>.Ok(await dnsService.UpdateTemplateAsync(request, cancellationToken), "dns.template_saved"));
}

/// <summary>
/// DNS sunucusunun dışarıya yanıt verebilirliği (53 portunun fiili dinleyicileri) + tek tık onarım.
/// Sunucu-geneli operasyonel işlem → admin-only. Sunucu Sağlığı ekranı bu uçları kullanır.
/// </summary>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/dns/server")]
public sealed class DnsServerController(IDnsService dnsService) : ControllerBase
{
    [HttpGet("health")]
    public async Task<ActionResult<ApiResponse<DnsServerHealthDto>>> GetHealthAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<DnsServerHealthDto>.Ok(await dnsService.GetServerHealthAsync(cancellationToken)));

    [HttpPost("repair")]
    public async Task<ActionResult<ApiResponse<DnsServerHealthDto>>> RepairAsync(
        [FromBody] RepairDnsServerRequest? request,
        CancellationToken cancellationToken)
    {
        var health = await dnsService.RepairServerAsync(request?.ApplyDelayedAutoStart ?? true, cancellationToken);
        return Ok(ApiResponse<DnsServerHealthDto>.Ok(health, "dns.server_repaired"));
    }
}

public sealed class RepairDnsServerRequest
{
    /// <summary>Servisi gecikmeli otomatik başlangıca da al (reboot yarış durumuna karşı). Varsayılan: açık.</summary>
    public bool ApplyDelayedAutoStart { get; init; } = true;
}
