using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// panel Server Health / Repair Kit benzeri: kritik servis durumları + tek-tık yeniden başlat (admin).
// (Anonim liveness ucu HealthController'da; bu admin-only operasyonel sağlık panosudur.)
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/server-health")]
public sealed class ServerHealthController(IHealthService healthService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ServiceHealthDto>>>> GetAsync(CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<IReadOnlyCollection<ServiceHealthDto>>.Ok(await healthService.GetHealthAsync(cancellationToken)));
    }

    [HttpPost("restart")]
    public async Task<ActionResult<ApiResponse<ServiceHealthDto>>> RestartAsync(
        [FromBody] RestartServiceRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<ServiceHealthDto>.Ok(await healthService.RestartServiceAsync(request.Name, cancellationToken), "health.restarted"));
    }
}
