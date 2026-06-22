using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Statistics;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites/{siteName}/statistics")]
public sealed class StatisticsController(
    IWebStatisticsService webStatisticsService,
    ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<WebStatisticsDto>>> GetAsync(
        string siteName,
        [FromQuery] int days = 7,
        CancellationToken cancellationToken = default)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var statistics = await webStatisticsService.GetSiteStatisticsAsync(siteName, days, cancellationToken);
        return Ok(ApiResponse<WebStatisticsDto>.Ok(statistics));
    }
}
