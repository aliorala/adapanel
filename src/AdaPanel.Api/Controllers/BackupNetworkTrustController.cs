using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/backup/network-trust")]
public sealed class BackupNetworkTrustController(BackupNetworkTrustService trust, IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<BackupNetworkTrustDto>>> GetAsync([FromQuery] string target, CancellationToken token)
    {
        await AuthorizeAsync(token);
        return Ok(ApiResponse<BackupNetworkTrustDto>.Ok(await trust.GetAsync(target, User.GetRequiredUserId(), false, token)));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<BackupNetworkTrustDto>>> SetAsync([FromBody] SetBackupNetworkTrustRequest request, CancellationToken token)
    {
        await AuthorizeAsync(token);
        var result = await trust.SetAsync(request.Target, request.Allowed, User.GetRequiredUserId(), false, token);
        await auditService.AuditAsync(HttpContext, "backup.network_trust.changed", "backup",
            $"Target={result.Target}; Allowed={result.Allowed}");
        return Ok(ApiResponse<BackupNetworkTrustDto>.Ok(result));
    }

    private async Task AuthorizeAsync(CancellationToken token)
    {
        try { await trust.RequireOwnerAsync(User.GetRequiredUserId(), User.HasClaim(c => c.Type == "impersonator_id"), token); }
        catch (AppException)
        {
            await auditService.AuditAsync(HttpContext, "backup.network_trust.denied", "backup", "Caller is not the server owner or is impersonating.");
            throw;
        }
    }
}
