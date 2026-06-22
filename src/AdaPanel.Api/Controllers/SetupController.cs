using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Setup;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Route("api/v1/setup")]
public sealed class SetupController(ISetupService setupService) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<SetupStatusDto>>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await setupService.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<SetupStatusDto>.Ok(status));
    }

    [HttpGet("progress")]
    public async Task<ActionResult<ApiResponse<OperationProgressDto>>> GetProgressAsync(CancellationToken cancellationToken)
    {
        var progress = await setupService.GetInstallProgressAsync(cancellationToken);
        return Ok(ApiResponse<OperationProgressDto>.Ok(progress));
    }

    [HttpPost("initialize")]
    [EnableRateLimiting("setup")]
    public async Task<ActionResult<ApiResponse<SetupResultDto>>> InitializeAsync(
        [FromBody] InitialSetupRequest request,
        CancellationToken cancellationToken)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp is null || !IPAddress.IsLoopback(remoteIp))
        {
            throw new AppException("setup.local_only", 403, 403);
        }

        var remoteIpAddress = remoteIp.ToString();
        var result = await setupService.InitializeAsync(request, remoteIpAddress, cancellationToken);

        return Ok(ApiResponse<SetupResultDto>.Ok(result, "setup.initialize.success"));
    }
}
