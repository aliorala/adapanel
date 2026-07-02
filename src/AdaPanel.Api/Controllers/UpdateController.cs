using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/update")]
public sealed class UpdateController(ISelfUpdateService selfUpdateService) : ControllerBase
{
    [HttpGet("check")]
    public async Task<ActionResult<ApiResponse<UpdateStatusDto>>> CheckAsync(CancellationToken cancellationToken)
    {
        var status = await selfUpdateService.CheckForUpdateAsync(cancellationToken);
        return Ok(ApiResponse<UpdateStatusDto>.Ok(status));
    }

    [HttpPost("apply")]
    public async Task<ActionResult<ApiResponse<UpdateStatusDto>>> ApplyAsync([FromBody] ApplyUpdateRequest request, CancellationToken cancellationToken)
    {
        var status = await selfUpdateService.ApplyUpdateAsync(request, cancellationToken);
        return Ok(ApiResponse<UpdateStatusDto>.Ok(status, "update.downloaded"));
    }

    [HttpPost("install")]
    public async Task<ActionResult<ApiResponse<UpdateStatusDto>>> InstallAsync(CancellationToken cancellationToken)
    {
        var status = await selfUpdateService.InstallPreparedUpdateAsync(cancellationToken);
        return Ok(ApiResponse<UpdateStatusDto>.Ok(status, "update.install_scheduled"));
    }

    // Güncellemelerin çekileceği GitHub deposunu (owner/repo) ayarlar — panel en son Release'i buradan otomatik görür.
    [HttpPost("github")]
    public async Task<ActionResult<ApiResponse<UpdateStatusDto>>> SetGithubAsync([FromBody] SetUpdateGithubRequest request, CancellationToken cancellationToken)
    {
        var status = await selfUpdateService.SetGithubRepoAsync(request.Repo, cancellationToken);
        return Ok(ApiResponse<UpdateStatusDto>.Ok(status, "update.github_saved"));
    }
}
