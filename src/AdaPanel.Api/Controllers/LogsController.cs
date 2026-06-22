using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Logs;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/logs")]
public sealed class LogsController(ILogViewerService logViewerService) : ControllerBase
{
    [HttpGet("panel")]
    public async Task<ActionResult<ApiResponse<LogTailDto>>> GetPanelLogAsync([FromQuery] int lines = 120, CancellationToken cancellationToken = default)
    {
        var log = await logViewerService.TailPanelLogAsync(lines, cancellationToken);
        return Ok(ApiResponse<LogTailDto>.Ok(log));
    }

    [HttpGet("site/{siteName}")]
    public async Task<ActionResult<ApiResponse<LogTailDto>>> GetSiteLogAsync(string siteName, [FromQuery] int lines = 120, CancellationToken cancellationToken = default)
    {
        var log = await logViewerService.TailSiteLogAsync(siteName, lines, cancellationToken);
        return Ok(ApiResponse<LogTailDto>.Ok(log));
    }
}
