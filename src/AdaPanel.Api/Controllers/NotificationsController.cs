using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/notify")]
public sealed class NotificationsController(INotificationService notificationService) : ControllerBase
{
    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<NotificationSettingsDto>>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await notificationService.GetSettingsAsync(cancellationToken);
        return Ok(ApiResponse<NotificationSettingsDto>.Ok(settings));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<NotificationSettingsDto>>> UpdateSettingsAsync([FromBody] UpdateNotificationSettingsRequest request, CancellationToken cancellationToken)
    {
        var settings = await notificationService.UpdateSettingsAsync(request, cancellationToken);
        return Ok(ApiResponse<NotificationSettingsDto>.Ok(settings, "notifications.settings_saved"));
    }

    [HttpPost("test")]
    public async Task<ActionResult<ApiResponse<object>>> TestAsync([FromBody] TestNotificationRequest request, CancellationToken cancellationToken)
    {
        await notificationService.TestAsync(request, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { request.Channel }, "notifications.test_sent"));
    }
}
