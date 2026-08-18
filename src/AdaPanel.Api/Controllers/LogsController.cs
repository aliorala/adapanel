using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Logs;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Logs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Günlük görüntüleme + günlük döndürme (rotation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Yetki neden sınıf düzeyinden alındı:</b> ASP.NET Core'da sınıf ve eylem üzerindeki
/// <see cref="AuthorizeAttribute"/> rolleri VE ile birleşir; sınıfta "Administrator" dururken bir
/// eyleme "Reseller" eklemek o eylemi bayiye AÇMAZ, tersine iki koşulu birden dayatır. Bu yüzden
/// sınıfta yalnız kimlik doğrulaması aranıyor ve rol kararı eylem başına veriliyor.
/// <b>Var olan iki uç (panel/site günlüğü kuyruğu) aynen "Administrator" kaldı</b> — bu değişiklik
/// hiçbir ucu genişletmiyor.
/// </para>
/// <para>
/// <b>Site kapsamı:</b> site adı alan her uç <see cref="CustomerAccess.EnsureSiteAsync"/>'den geçer.
/// Kapsamsız roller (yönetici/operatör) için işlemsizdir; kapsamlı rolde (müşteri/bayi) yabancı bir
/// site adı 403 döner. Guard, rolün ileride genişlemesi ihtimaline karşı bırakılan bir süs değil:
/// döndürme uçları bayiye AÇIK ve bayinin kapsamı buradan uygulanıyor.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/v1/logs")]
public sealed class LogsController(
    ILogViewerService logViewerService,
    ILogRotationService logRotationService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("panel")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<LogTailDto>>> GetPanelLogAsync([FromQuery] int lines = 120, CancellationToken cancellationToken = default)
    {
        var log = await logViewerService.TailPanelLogAsync(lines, cancellationToken);
        return Ok(ApiResponse<LogTailDto>.Ok(log));
    }

    [HttpGet("site/{siteName}")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<LogTailDto>>> GetSiteLogAsync(string siteName, [FromQuery] int lines = 120, CancellationToken cancellationToken = default)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var log = await logViewerService.TailSiteLogAsync(siteName, lines, cancellationToken);
        return Ok(ApiResponse<LogTailDto>.Ok(log));
    }

    [HttpGet("rotation")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<LogRotationStatusDto>>> GetRotationAsync(CancellationToken cancellationToken)
    {
        var status = await logRotationService.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<LogRotationStatusDto>.Ok(status));
    }

    [HttpPut("rotation")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<LogRotationStatusDto>>> UpdateRotationAsync(
        [FromBody] UpdateLogRotationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var status = await logRotationService.UpdateSettingsAsync(request, cancellationToken);
        await auditService.AuditAsync(
            HttpContext,
            "logs.rotation_settings_updated",
            "logs",
            $"Log rotation policy updated (enabled={request.Enabled}, compress={request.Compress}, "
            + $"siteAgeDays={request.SiteLogMaxAgeDays}, panelAgeDays={request.PanelLogMaxAgeDays}, "
            + $"maxFileMB={request.MaxFileSizeMB}, siteKeep={request.SiteKeepCount}, "
            + $"panelKeep={request.PanelKeepCount}, maxTotalMB={request.MaxTotalSizeMB}).");

        return Ok(ApiResponse<LogRotationStatusDto>.Ok(status, "logs.rotation_saved"));
    }

    [HttpPost("rotation/run")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<LogRotationStatusDto>>> RunRotationAsync(CancellationToken cancellationToken)
    {
        var status = await logRotationService.RunAsync(null, cancellationToken);
        await auditService.AuditAsync(
            HttpContext,
            "logs.rotation_run",
            "logs",
            "Log rotation was triggered manually for every managed directory.");

        return Ok(ApiResponse<LogRotationStatusDto>.Ok(status, "logs.rotation_completed"));
    }

    [HttpGet("rotation/site/{siteName}")]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<LogRotationTargetDto>>> GetSiteRotationAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var target = await logRotationService.GetSiteStatusAsync(siteName, cancellationToken);
        return Ok(ApiResponse<LogRotationTargetDto>.Ok(target));
    }

    [HttpPost("rotation/site/{siteName}/run")]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<LogRotationTargetDto>>> RunSiteRotationAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        // KAPSAM ÖNCE: dosya silen bir iş, site adının çağırana ait olduğu doğrulanmadan başlamaz.
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        await logRotationService.RunAsync(siteName, cancellationToken);
        var target = await logRotationService.GetSiteStatusAsync(siteName, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "logs.rotation_run",
            "logs",
            $"Log rotation was triggered manually for '{siteName}': "
            + $"{target.RotatedFiles} rotated, {target.DeletedArchives} archive(s) deleted.");

        return Ok(ApiResponse<LogRotationTargetDto>.Ok(target, "logs.rotation_completed"));
    }
}
