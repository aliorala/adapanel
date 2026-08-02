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
    /// <param name="force">
    /// Kullanıcı "kontrol et" düğmesine bastığında true: önbellek atlanır, GitHub'a gidilir.
    /// Varsayılan false, çünkü bu uç aynı zamanda panel açılışında ve rozet yenilemelerinde
    /// çağrılıyor; her seferinde ağa çıkmak GitHub'ın kimliksiz saatlik sınırını (60 istek)
    /// doldurur ve o noktadan sonra yeni sürüm YAYINLANSA BİLE görünmez olur.
    /// </param>
    [HttpGet("check")]
    public async Task<ActionResult<ApiResponse<UpdateStatusDto>>> CheckAsync(
        CancellationToken cancellationToken,
        [FromQuery] bool force = false)
    {
        var status = await selfUpdateService.CheckForUpdateAsync(force, cancellationToken);
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
