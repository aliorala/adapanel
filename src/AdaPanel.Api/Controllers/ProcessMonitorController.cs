using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Monitoring;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Canlı süreç/kaynak izleyici (htop benzeri). Süreç listesi tüm sunucuyu ifşa ettiği için
/// yalnız Administrator erişebilir — müşteri/bayi rolleri diğer kiracıların süreçlerini görmemeli.
/// </summary>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/monitoring/processes")]
public sealed class ProcessMonitorController(IProcessMonitorService processMonitorService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<ProcessSnapshotDto>>> GetAsync(
        [FromQuery] int limit = 30,
        [FromQuery] string sortBy = "cpu",
        CancellationToken cancellationToken = default)
    {
        var snapshot = await processMonitorService.GetSnapshotAsync(limit, sortBy, cancellationToken);
        return Ok(ApiResponse<ProcessSnapshotDto>.Ok(snapshot));
    }
}
