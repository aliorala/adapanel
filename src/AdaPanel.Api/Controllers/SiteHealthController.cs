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
public sealed class SiteHealthController(ISiteHealthService siteHealthService, ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SiteHealthDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteHealthDto>.Ok(await siteHealthService.GetSiteHealthAsync(siteName, cancellationToken)));
    }
}
