using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/sites/{siteName}/quota")]
public sealed class DiskQuotaController(IDiskQuotaService diskQuotaService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SiteDiskQuotaDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        var quota = await diskQuotaService.GetQuotaAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<SiteDiskQuotaDto>>> SetAsync(
        string siteName,
        [FromBody] SetSiteDiskQuotaRequest request,
        CancellationToken cancellationToken)
    {
        var quota = await diskQuotaService.SetQuotaAsync(siteName, request, cancellationToken);
        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota, "quota.saved"));
    }

    [HttpDelete]
    public async Task<ActionResult<ApiResponse<SiteDiskQuotaDto>>> RemoveAsync(string siteName, CancellationToken cancellationToken)
    {
        var quota = await diskQuotaService.RemoveQuotaAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota, "quota.removed"));
    }

    /// <summary>Disk kotası için gereken Windows rolünü (FSRM) kurar — gerekçe: IDiskQuotaService.</summary>
    [HttpPost("install")]
    public async Task<ActionResult<ApiResponse<SiteDiskQuotaDto>>> InstallFsrmAsync(string siteName, CancellationToken cancellationToken)
    {
        var quota = await diskQuotaService.InstallFsrmAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota, "quota.fsrm_installed"));
    }

}
