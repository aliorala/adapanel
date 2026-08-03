using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Sitenin DIŞARIDAN görünen sağlığı. Reseller (Bayi) yalnız kendi müşterilerinin sitelerini
// ölçebilir → DnsController ile AYNI kapsam denetimi (CustomerAccess.EnsureSiteAsync).
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites/{siteName}/health")]
public sealed class SiteHealthController(
    ISiteHealthService siteHealthService,
    IDomainRefreshService domainRefreshService,
    ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SiteHealthDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteHealthDto>.Ok(await siteHealthService.GetSiteHealthAsync(siteName, cancellationToken)));
    }

    /// <summary>
    /// Bu sitede onarılacak bir şey var mı? Hiçbir şey DEĞİŞTİRMEZ.
    /// <para>
    /// Site detay sayfası bunu okuyup uyarı gösterir. Öncesinde yalnız sunucu geneli bir denetim
    /// vardı: kullanıcı "2 alan adı eski" yazısını görüyor ama HANGİ site olduğunu öğrenemiyordu.
    /// </para>
    /// </summary>
    [HttpGet("repair")]
    public async Task<ActionResult<ApiResponse<DomainRefreshResultDto>>> InspectRepairAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<DomainRefreshResultDto>.Ok(await domainRefreshService.InspectAsync(siteName, cancellationToken)));
    }

    /// <summary>Bu sitenin eksiklerini tamamlar ve NE YAPILDIĞINI döner.</summary>
    [HttpPost("repair")]
    public async Task<ActionResult<ApiResponse<DomainRefreshResultDto>>> RepairAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await domainRefreshService.RefreshAsync(siteName, cancellationToken);
        return Ok(ApiResponse<DomainRefreshResultDto>.Ok(result, "health.domains_refreshed"));
    }
}
