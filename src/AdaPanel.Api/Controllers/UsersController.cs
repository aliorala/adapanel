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
}
