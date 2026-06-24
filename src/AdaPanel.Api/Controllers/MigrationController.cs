using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Plesk/cPanel/ham sunucu → AdaPanel site göçü (tek akış): site oluştur + dosya çek (FTP/SFTP) + DB aktar.
// Admin-only. Her adım bağımsız → kısmi başarı olabilir; sonuç adım-adım döner.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/migration")]
public sealed class MigrationController(ISiteMigrationService migrationService) : ControllerBase
{
    [HttpPost("site")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<ApiResponse<MigrateSiteResultDto>>> MigrateSiteAsync(
        [FromBody] MigrateSiteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await migrationService.MigrateSiteAsync(
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<MigrateSiteResultDto>.Ok(
            result,
            result.OverallSuccess ? "migration.site.success" : "migration.site.partial"));
    }
}
