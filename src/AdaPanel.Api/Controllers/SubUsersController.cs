using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Plesk "additional users" paritesi: TOP-LEVEL müşteri kendi aboneliği altında ek login'ler açar/yönetir.
// Alt-kullanıcının kendi kaynağı yoktur (site/kota/cron/DB abonelikten miras); izin matrisi bölüm bazlıdır.
// Aktörün top-level müşteri olduğu servis katmanında doğrulanır (alt-kullanıcı bu ucu KULLANAMAZ → 403).
[ApiController]
[Authorize(Roles = "Customer")]
[Route("api/v1/sub-users")]
public sealed class SubUsersController(IUserManagementService userManagementService) : ControllerBase
{
    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SubUserDto>>>> GetSubUsersAsync(CancellationToken cancellationToken)
    {
        var subUsers = await userManagementService.GetSubUsersAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<SubUserDto>>.Ok(subUsers));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<SubUserDto>>> CreateSubUserAsync(
        [FromBody] CreateSubUserRequest request,
        CancellationToken cancellationToken)
    {
        var subUser = await userManagementService.CreateSubUserAsync(User.GetRequiredUserId(), request, RemoteIp, cancellationToken);
        return Ok(ApiResponse<SubUserDto>.Ok(subUser, "users.sub_create.success"));
    }

    [HttpPut("{subUserId:guid}")]
    public async Task<ActionResult<ApiResponse<SubUserDto>>> UpdateSubUserAsync(
        Guid subUserId,
        [FromBody] UpdateSubUserRequest request,
        CancellationToken cancellationToken)
    {
        var subUser = await userManagementService.UpdateSubUserAsync(User.GetRequiredUserId(), subUserId, request, RemoteIp, cancellationToken);
        return Ok(ApiResponse<SubUserDto>.Ok(subUser, "users.sub_update.success"));
    }

    [HttpDelete("{subUserId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteSubUserAsync(
        Guid subUserId,
        CancellationToken cancellationToken)
    {
        await userManagementService.DeleteSubUserAsync(User.GetRequiredUserId(), subUserId, RemoteIp, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { id = subUserId }, "users.sub_delete.success"));
    }
}
