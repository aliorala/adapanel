using AdaPanel.Core.Dtos.Advisor;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/advisor")]
public sealed class AdvisorController(IAdvisorService advisorService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<AdvisorReportDto>>> GetReportAsync(CancellationToken cancellationToken)
    {
        var report = await advisorService.GetReportAsync(cancellationToken);
        return Ok(ApiResponse<AdvisorReportDto>.Ok(report));
    }
}
