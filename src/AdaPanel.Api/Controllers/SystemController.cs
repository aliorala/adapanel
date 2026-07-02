using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/system")]
public sealed class SystemController(ISystemMonitoringService systemMonitoringService) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<ActionResult<ApiResponse<SystemOverviewDto>>> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var overview = await systemMonitoringService.GetOverviewAsync(cancellationToken);
        return Ok(ApiResponse<SystemOverviewDto>.Ok(overview));
    }

    [HttpGet("ip-addresses")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ServerIpAddressDto>>>> GetIpAddressesAsync(CancellationToken cancellationToken)
    {
        var addresses = await systemMonitoringService.GetServerIpAddressesAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<ServerIpAddressDto>>.Ok(addresses));
    }

    // Kalıcı izleme geçmişi (CPU/RAM/ağ örnekleri) → izleme ekranı trend grafikleri.
    [HttpGet("history")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SystemHistorySampleDto>>>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        var history = await systemMonitoringService.GetHistoryAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<SystemHistorySampleDto>>.Ok(history));
    }
}
