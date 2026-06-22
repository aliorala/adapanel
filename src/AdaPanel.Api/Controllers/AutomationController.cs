using AdaPanel.Core.Dtos.Automation;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/automation")]
public sealed class AutomationController(IAutomationService automationService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<AutomationStatusDto>>> GetAsync(CancellationToken cancellationToken)
    {
        var status = await automationService.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<AutomationStatusDto>.Ok(status));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<AutomationStatusDto>>> UpdateAsync(
        [FromBody] UpdateAutomationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var status = await automationService.UpdateSettingsAsync(request, cancellationToken);
        return Ok(ApiResponse<AutomationStatusDto>.Ok(status, "automation.settings_saved"));
    }

    [HttpPost("backup/run")]
    public async Task<ActionResult<ApiResponse<AutomationStatusDto>>> RunBackupAsync(CancellationToken cancellationToken)
    {
        var status = await automationService.RunBackupNowAsync(cancellationToken);
        return Ok(ApiResponse<AutomationStatusDto>.Ok(status, "automation.backup_started"));
    }

    [HttpPost("ssl/run")]
    public async Task<ActionResult<ApiResponse<AutomationStatusDto>>> RunSslAsync(CancellationToken cancellationToken)
    {
        var status = await automationService.RunSslRenewNowAsync(cancellationToken);
        return Ok(ApiResponse<AutomationStatusDto>.Ok(status, "automation.ssl_started"));
    }
}
