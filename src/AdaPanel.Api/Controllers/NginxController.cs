using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Nginx;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/nginx")]
public sealed class NginxController(INginxService nginxService) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<NginxStatusDto>>> GetStatusAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<NginxStatusDto>.Ok(await nginxService.GetStatusAsync(cancellationToken)));

    [HttpPost("configure")]
    public async Task<ActionResult<ApiResponse<NginxStatusDto>>> ConfigureAsync(
        [FromBody] NginxConfigurationRequest request,
        CancellationToken cancellationToken)
        => Ok(ApiResponse<NginxStatusDto>.Ok(
            await nginxService.ConfigureAsync(
                request,
                User.GetRequiredUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken),
            "nginx.configuration_updated"));

    [HttpPost("start")]
    public async Task<ActionResult<ApiResponse<NginxStatusDto>>> StartAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<NginxStatusDto>.Ok(
            await nginxService.StartAsync(
                User.GetRequiredUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken),
            "nginx.started"));

    [HttpPost("stop")]
    public async Task<ActionResult<ApiResponse<NginxStatusDto>>> StopAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<NginxStatusDto>.Ok(
            await nginxService.StopAsync(
                User.GetRequiredUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken),
            "nginx.stopped"));

    [HttpPost("reload")]
    public async Task<ActionResult<ApiResponse<NginxStatusDto>>> ReloadAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<NginxStatusDto>.Ok(
            await nginxService.ReloadAsync(
                User.GetRequiredUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken),
            "nginx.reloaded"));

    [HttpPost("test")]
    public async Task<ActionResult<ApiResponse<NginxStatusDto>>> TestAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<NginxStatusDto>.Ok(
            await nginxService.TestConfigurationAsync(cancellationToken),
            "nginx.config_valid"));
}
