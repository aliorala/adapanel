using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/users")]
public sealed class UsersController(IUserManagementService userManagementService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<UserSummaryDto>>>> GetUsersAsync(CancellationToken cancellationToken)
    {
        // Bayi (Reseller) ise servis yalnızca kendi müşterilerini döner.
        var users = await userManagementService.GetUsersAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<UserSummaryDto>>.Ok(users));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<UserSummaryDto>>> CreateUserAsync(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userManagementService.CreateUserAsync(
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<UserSummaryDto>.Ok(user, "users.create.success"));
    }

    /// <summary>
    /// Kullanıcıyı siler. removeSites=true (yalnız admin): atanan siteler dosya + eşleşen DB'lerle
    /// cascade silinir; aksi halde siteler/veriler sunucuda kalır ve yönetime devredilmiş olur.
    /// </summary>
    [HttpDelete("{userId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteUserAsync(
        Guid userId,
        [FromQuery] bool removeSites,
        CancellationToken cancellationToken)
    {
        if (removeSites && !User.IsInRole("Administrator"))
        {
            throw new AdaPanel.Core.Common.AppException("users.forbidden", 403, 403);
        }

        await userManagementService.DeleteUserAsync(
            userId,
            User.GetRequiredUserId(),
            removeSites,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { id = userId }, "users.delete.success"));
    }

    [HttpPut("{userId:guid}")]
    public async Task<ActionResult<ApiResponse<UserSummaryDto>>> UpdateUserAsync(
        Guid userId,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userManagementService.UpdateUserAsync(
            userId,
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<UserSummaryDto>.Ok(user, "users.update.success"));
    }

    /// <summary>
    /// Hatalı giriş denemeleri yüzünden kilitlenmiş kullanıcının kilidini açar. Kilitli kullanıcı
    /// süre dolana kadar (varsayılan 15 dk) giriş yapamaz; bu uç beklemeden açar. Bayi yalnız kendi
    /// müşterisini açabilir. HİÇBİR yönetici giremiyorsa: veri kökünde config\recover-access.flag.
    /// </summary>
    [HttpPost("{userId:guid}/unlock")]
    public async Task<ActionResult<ApiResponse<UserSummaryDto>>> UnlockUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await userManagementService.UnlockUserAsync(
            userId,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<UserSummaryDto>.Ok(user, "users.unlock.success"));
    }
}
