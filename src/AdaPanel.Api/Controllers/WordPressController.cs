using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız kendi müşterilerinin sitelerindeki WordPress kurulumlarını görür/yönetir.
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/wordpress")]
public sealed class WordPressController(
    IWordPressToolkitService wordPressToolkitService,
    ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<WordPressSiteDto>>>> GetAllAsync(CancellationToken cancellationToken)
    {
        var installs = await wordPressToolkitService.DetectAllAsync(cancellationToken);

        // Bayi/müşteri yalnızca etkin kapsamındaki sitelerin WP kurulumlarını görür.
        var allowed = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, User, cancellationToken);
        if (allowed is not null)
        {
            installs = installs
                .Where(item => allowed.Contains(item.SiteName, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        return Ok(ApiResponse<IReadOnlyCollection<WordPressSiteDto>>.Ok(installs));
    }

    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<WordPressSiteDto>>> DetectAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var install = await wordPressToolkitService.DetectAsync(siteName, cancellationToken);
        return Ok(ApiResponse<WordPressSiteDto>.Ok(install));
    }

    [HttpPost("{siteName}/maintenance")]
    public async Task<ActionResult<ApiResponse<WordPressSiteDto>>> SetMaintenanceAsync(
        string siteName,
        [FromBody] SetWordPressMaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var install = await wordPressToolkitService.SetMaintenanceAsync(siteName, request.Enabled, cancellationToken);
        return Ok(ApiResponse<WordPressSiteDto>.Ok(install, "wp.maintenance_updated"));
    }
}
