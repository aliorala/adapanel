using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/audit-logs")]
public sealed class AuditLogsController(IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AuditLogDto>>>> GetRecentAsync(
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default)
    {
        var items = await auditService.GetRecentAsync(take, cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<AuditLogDto>>.Ok(items));
    }
}
