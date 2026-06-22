using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Oturumdaki kullanıcının rolüne göre erişebildiği siteleri döner:
/// Administrator tümünü, Customer yalnızca atanmış olanları görür.
/// Dosya/yedek/e-posta ekranlarındaki site listeleri bu ucu kullanır.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/scope")]
public sealed class ScopeController(
    IIisSiteService iisSiteService,
    ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet("sites")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SiteDto>>>> GetSitesAsync(CancellationToken cancellationToken)
    {
        if (User.IsInRole("Administrator"))
        {
            var all = await iisSiteService.GetSitesAsync(cancellationToken);
            return Ok(ApiResponse<IReadOnlyCollection<SiteDto>>.Ok(all));
        }

        if (!CustomerAccess.IsCustomer(User))
        {
            return Ok(ApiResponse<IReadOnlyCollection<SiteDto>>.Ok([]));
        }

        var assigned = await customerScopes.GetAssignedSitesAsync(User.GetRequiredUserId(), cancellationToken);
        var sites = new List<SiteDto>(assigned.Count);
        foreach (var siteName in assigned)
        {
            try
            {
                sites.Add(await iisSiteService.GetSiteAsync(siteName, cancellationToken));
            }
            catch
            {
                // Site IIS'te artık yoksa listeden düşer.
            }
        }

        return Ok(ApiResponse<IReadOnlyCollection<SiteDto>>.Ok(sites));
    }
}
