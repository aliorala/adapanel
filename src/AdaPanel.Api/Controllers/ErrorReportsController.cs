using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Diagnostics;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Otomatik hata bildirimi yüzeyi. Kayıtlar MASKELENMİŞ olsa da yığın izi + rota şablonu
/// sunucunun iç yapısını gösterir; bu yüzden yalnız Administrator erişir — müşteri/bayi
/// başka kiracıların hatalarını görmemeli.
/// </summary>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/error-reports")]
public sealed class ErrorReportsController(
    IErrorReportService errorReportService,
    IErrorReportSender errorReportSender,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ErrorReportDto>>>> GetRecentAsync(
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var reports = await errorReportService.GetRecentAsync(take, cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<ErrorReportDto>>.Ok(reports));
    }

    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<ErrorReportingSettingsDto>>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await errorReportSender.GetSettingsAsync(cancellationToken);
        return Ok(ApiResponse<ErrorReportingSettingsDto>.Ok(settings));
    }

    /// <summary>
    /// Onayı ve toplayıcı adresini günceller. Onayın açılıp kapanması denetim kaydına düşer:
    /// "veri dışarı çıkıyor mu" sorusunun cevabı sonradan izlenebilir olmalı.
    /// </summary>
    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<ErrorReportingSettingsDto>>> UpdateSettingsAsync(
        [FromBody] ErrorReportingSettingsDto request,
        CancellationToken cancellationToken)
    {
        var settings = await errorReportSender.UpdateSettingsAsync(request, cancellationToken);
        var collectorForAudit = string.IsNullOrWhiteSpace(settings.CollectorUrl)
            ? "not configured"
            : AuditSecretRedactor.RedactUrl(settings.CollectorUrl);

        await WriteAuditAsync(
            "error_reports.settings_updated",
            $"Automatic error reporting {(settings.Enabled ? "enabled" : "disabled")}; collector "
                + collectorForAudit
                + $"; retention {settings.RetentionDays} day(s).",
            cancellationToken);

        return Ok(ApiResponse<ErrorReportingSettingsDto>.Ok(settings, "errorReporting.settings_saved"));
    }

    /// <summary>Bekleyen kayıtları hemen gönderir (otomasyon turunu beklemeden).</summary>
    [HttpPost("send")]
    public async Task<ActionResult<ApiResponse<ErrorReportSendResultDto>>> SendPendingAsync(
        [FromBody] SendErrorReportsRequest request, CancellationToken cancellationToken)
    {
        var revision = request?.ExpectedSettingsRevision;
        if (string.IsNullOrWhiteSpace(revision))
            throw new AppException("errorReporting.settings_revision_required", 400, 400);
        var sentCount = await errorReportSender.SendPendingAsync(revision, cancellationToken);

        // Gönderim yapılmadıysa (onay kapalı / adres yok / bekleyen yok) denetim kaydı şişirilmez.
        if (sentCount > 0)
        {
            await WriteAuditAsync(
                "error_reports.sent",
                $"{sentCount} masked error report(s) sent to the collector on demand.",
                cancellationToken);
        }

        return Ok(ApiResponse<ErrorReportSendResultDto>.Ok(
            new ErrorReportSendResultDto { SentCount = sentCount },
            sentCount > 0 ? "errorReporting.sent" : "errorReporting.nothing_to_send"));
    }

    private Task WriteAuditAsync(string action, string details, CancellationToken cancellationToken) =>
        auditService.WriteAsync(
            action,
            "error-reports",
            details,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
}

/// <summary>"Şimdi gönder" sonucunun arayüze dönen gövdesi.</summary>
public sealed class ErrorReportSendResultDto
{
    public int SentCount { get; init; }
}
