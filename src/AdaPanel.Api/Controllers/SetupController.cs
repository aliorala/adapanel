using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Setup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: `initialize` panelin İLK YÖNETİCİSİNİ yaratır — yani sunucudaki en yetkili hesabın
// doğum kaydıdır. Bu uç KİMLİK DOĞRULAMASIZDIR (yalnız loopback + hız sınırı ile korunur), dolayısıyla
// kayıtta kullanıcı "system" görünür; geriye kalan tek tanımlayıcı İP adresidir ve onu
// HttpAuditExtensions her kayda yazar. Bu iz yoksa "bu yönetici hesabı ne zaman ve nereden açıldı"
// sorusunun cevabı hiçbir yerde yoktur — kurulum sonrası ilk sorulacak soru tam olarak budur.
//
// Parola elbette yazılmaz; yalnız kullanıcı adı ve kurulumun ÖLÇÜLEN sonucu.
[ApiController]
[Route("api/v1/setup")]
public sealed class SetupController(
    ISetupService setupService,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<SetupStatusDto>>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await setupService.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<SetupStatusDto>.Ok(status));
    }

    [HttpGet("progress")]
    public async Task<ActionResult<ApiResponse<OperationProgressDto>>> GetProgressAsync(CancellationToken cancellationToken)
    {
        var progress = await setupService.GetInstallProgressAsync(cancellationToken);
        return Ok(ApiResponse<OperationProgressDto>.Ok(progress));
    }

    [HttpPost("initialize")]
    [EnableRateLimiting("setup")]
    public async Task<ActionResult<ApiResponse<SetupResultDto>>> InitializeAsync(
        [FromBody] InitialSetupRequest request,
        CancellationToken cancellationToken)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp is null || !IPAddress.IsLoopback(remoteIp))
        {
            throw new AppException("setup.local_only", 403, 403);
        }

        var remoteIpAddress = remoteIp.ToString();
        var result = await setupService.InitializeAsync(request, remoteIpAddress, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "setup.initialized",
            "panel",
            $"Panel initialized: administrator '{result.AdminUserName}' created, "
            + $"secure entry path '{result.SecureEntryPath}', https port {result.PreferredHttpsPort}, "
            + $"recommended components requested: {result.RecommendedComponentsRequested}.");

        return Ok(ApiResponse<SetupResultDto>.Ok(result, "setup.initialize.success"));
    }
}
