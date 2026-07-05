using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Antivirus;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// ClamAV antivirüs (Plesk antivirüs paritesi). Admin-only: tarama sunucu genelinde dosya okur,
// kurulum yazılım kataloğuna yazar. Kurulum SoftwareController'la aynı bootstrapper'ı kullanır.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/antivirus")]
public sealed class AntivirusController(
    IAntivirusService antivirusService,
    IComponentBootstrapper componentBootstrapper,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<AntivirusStatusDto>>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await antivirusService.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<AntivirusStatusDto>.Ok(status));
    }

    [HttpPost("install")]
    public async Task<ActionResult<ApiResponse<AntivirusStatusDto>>> InstallAsync(CancellationToken cancellationToken)
    {
        await componentBootstrapper.InstallAsync(
            "clamav",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        var status = await antivirusService.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<AntivirusStatusDto>.Ok(status, "antivirus.installed"));
    }

    /// <summary>Virüs tanımlarını günceller (ilk sefer ~250 MB indirme; dakikalar sürebilir).</summary>
    [HttpPost("definitions/update")]
    public async Task<ActionResult<ApiResponse<AntivirusStatusDto>>> UpdateDefinitionsAsync(CancellationToken cancellationToken)
    {
        var status = await antivirusService.UpdateDefinitionsAsync(cancellationToken);
        await WriteAuditAsync("antivirus.definitions_updated", "Virus definitions updated via freshclam.", cancellationToken);
        return Ok(ApiResponse<AntivirusStatusDto>.Ok(status, "antivirus.definitions_ready"));
    }

    [HttpPost("scan")]
    public async Task<ActionResult<ApiResponse<AntivirusScanResultDto>>> ScanAsync(
        [FromBody] AntivirusScanRequest request,
        CancellationToken cancellationToken)
    {
        var result = await antivirusService.ScanAsync(request, cancellationToken);
        await WriteAuditAsync(
            "antivirus.scan_completed",
            $"Scan of '{result.Target}': {result.Status}, {result.InfectedCount} finding(s), {result.ScannedFiles} file(s).",
            cancellationToken);
        return Ok(ApiResponse<AntivirusScanResultDto>.Ok(result, "antivirus.scan_done"));
    }

    private Task WriteAuditAsync(string action, string details, CancellationToken cancellationToken) =>
        auditService.WriteAsync(
            action,
            "antivirus",
            details,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
}
