using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// panel Server Health / Repair Kit benzeri: kritik servis durumları + tek-tık yeniden başlat (admin).
// (Anonim liveness ucu HealthController'da; bu admin-only operasyonel sağlık panosudur.)
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/server-health")]
public sealed class ServerHealthController(
    IHealthService healthService,
    IDomainRefreshService domainRefreshService) : ControllerBase
{
    /// <summary>
    /// Sunucudaki TÜM alan adlarını panelin güncel varsayılanlarına taşır (eksikleri ekler, var
    /// olanı değiştirmez). Panel güncellendiğinde eski alan adları yeni ayarları almadığı için
    /// gerekli; ne eklendiği alan adı bazında raporlanır.
    /// </summary>
    [HttpPost("refresh-domains")]
    public async Task<ActionResult<ApiResponse<DomainRefreshSummaryDto>>> RefreshDomainsAsync(CancellationToken cancellationToken)
    {
        var summary = await domainRefreshService.RefreshAllAsync(cancellationToken);
        return Ok(ApiResponse<DomainRefreshSummaryDto>.Ok(summary, "health.domains_refreshed"));
    }

    /// <summary>Değişiklik yapmadan kaç alan adının eski kaldığını söyler (uyarı rozeti için).</summary>
    [HttpGet("refresh-domains")]
    public async Task<ActionResult<ApiResponse<DomainRefreshSummaryDto>>> InspectDomainsAsync(CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<DomainRefreshSummaryDto>.Ok(await domainRefreshService.InspectAllAsync(cancellationToken)));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ServiceHealthDto>>>> GetAsync(CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<IReadOnlyCollection<ServiceHealthDto>>.Ok(await healthService.GetHealthAsync(cancellationToken)));
    }

    [HttpPost("restart")]
    public async Task<ActionResult<ApiResponse<ServiceHealthDto>>> RestartAsync(
        [FromBody] RestartServiceRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<ServiceHealthDto>.Ok(await healthService.RestartServiceAsync(request.Name, cancellationToken), "health.restarted"));
    }
}
