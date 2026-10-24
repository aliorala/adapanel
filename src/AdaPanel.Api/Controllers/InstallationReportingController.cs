using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Diagnostics;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/installation-reporting")]
public sealed class InstallationReportingController(InstallationReportingService reporting, IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<InstallationReportingDto>>> Get(CancellationToken ct)
        => Ok(ApiResponse<InstallationReportingDto>.Ok(await reporting.GetAsync(ct)));

    [HttpPut]
    public async Task<ActionResult<ApiResponse<InstallationReportingDto>>> Update(UpdateInstallationReportingRequest request, CancellationToken ct)
    {
        var result = await reporting.UpdateAsync(request, ct);
        await auditService.AuditAsync(HttpContext, "installation.reporting.changed", "panel",
            $"Installation reporting enabled: {result.Enabled}.");
        return Ok(ApiResponse<InstallationReportingDto>.Ok(result));
    }
}
