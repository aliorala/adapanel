using AdaPanel.Core.Dtos.Automation;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: otomasyon ayarları, sunucunun KENDİ KENDİNİ KORUMA düzeneğidir — otomatik yedek ve
// otomatik sertifika yenileme. Bunları kapatmak ya da saklama süresini kısaltmak HEMEN hiçbir şey
// bozmaz; etkisi haftalar sonra, "geri dönecek yedek yok" ya da "sertifika süresi doldu, site
// açılmıyor" anında görünür. Tam da bu gecikme yüzünden iz şart: o an sorulacak tek soru "bu ayar ne
// zaman ve kim tarafından değiştirildi" olur ve ayar dosyası yalnız son hâli tutar.
//
// Saklama süresinin (retention) DÜŞÜRÜLMESİ ayrıca YIKICIDIR: bir sonraki budama turu, yeni süreye
// göre eski yedekleri SİLER. Bu yüzden eski→yeni değer birlikte yazılır.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/automation")]
public sealed class AutomationController(
    IAutomationService automationService,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<AutomationStatusDto>>> GetAsync(CancellationToken cancellationToken)
    {
        var status = await automationService.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<AutomationStatusDto>.Ok(status));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<AutomationStatusDto>>> UpdateAsync(
        [FromBody] UpdateAutomationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var oncesi = await automationService.GetStatusAsync(cancellationToken);
        var status = await automationService.UpdateSettingsAsync(request, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "automation.settings.updated",
            "automation",
            $"Automation settings changed. Auto backup {oncesi.Settings.AutoBackupEnabled} → {status.Settings.AutoBackupEnabled} "
            + $"(schedule '{oncesi.Settings.BackupScheduleMode}' → '{status.Settings.BackupScheduleMode}', "
            + $"retention {oncesi.Settings.BackupRetentionDays} → {status.Settings.BackupRetentionDays} days); "
            + $"auto SSL renewal {oncesi.Settings.AutoSslRenewEnabled} → {status.Settings.AutoSslRenewEnabled}.");

        return Ok(ApiResponse<AutomationStatusDto>.Ok(status, "automation.settings_saved"));
    }

    [HttpPost("backup/run")]
    public async Task<ActionResult<ApiResponse<AutomationStatusDto>>> RunBackupAsync(CancellationToken cancellationToken)
    {
        var status = await automationService.RunBackupNowAsync(cancellationToken);

        // Elle tetiklenen yedek turu, zamanlanmış turla AYNI budamayı yapar: saklama süresini aşan
        // eski yedekleri siler. Yani "yedek al" düğmesi aynı zamanda bir SİLME işlemidir.
        await auditService.AuditAsync(
            HttpContext,
            "automation.backup.run",
            "automation",
            $"Backup automation run manually; result: {status.LastBackupStatus}. {status.LastBackupDetails}");

        return Ok(ApiResponse<AutomationStatusDto>.Ok(status, "automation.backup_started"));
    }

    [HttpPost("ssl/run")]
    public async Task<ActionResult<ApiResponse<AutomationStatusDto>>> RunSslAsync(CancellationToken cancellationToken)
    {
        var status = await automationService.RunSslRenewNowAsync(cancellationToken);

        // Let's Encrypt hız sınırı vardır: elle tetiklenen yenileme turları sunucuyu sınıra
        // çarptırıp GERÇEK yenilemeleri günlerce engelleyebilir. Kimin kaç kez tetiklediği ancak
        // buradan görülür.
        await auditService.AuditAsync(
            HttpContext,
            "automation.ssl.run",
            "automation",
            $"SSL renewal automation run manually; result: {status.LastSslStatus}. {status.LastSslDetails}");

        return Ok(ApiResponse<AutomationStatusDto>.Ok(status, "automation.ssl_started"));
    }

    // Off-site yedek hedefini gerçek yedek almadan test et (küçük dosya yükleme). Parola boşsa kayıtlı kullanılır.
    [HttpPost("backup/remote/test")]
    public async Task<ActionResult<ApiResponse<BackupRemoteTestResultDto>>> TestRemoteBackupAsync(
        [FromBody] BackupRemoteTestRequest request,
        CancellationToken cancellationToken)
    {
        var result = await automationService.TestRemoteBackupAsync(request, cancellationToken);

        // "Test" adı zararsız görünür ama bu uç, panelin KAYITLI kimlik bilgileriyle dışarıdaki bir
        // hedefe DOSYA YÜKLER. İstekteki adresi değiştirip testi çalıştırmak, kayıtlı parolayı
        // saldırganın kendi sunucusuna karşı denemenin yoludur — yani bir sır sızdırma denemesidir
        // ve iz bırakmadan yapılabiliyor olması kabul edilemez. Hedef ADRESİ yazılır, PAROLA yazılmaz.
        await auditService.AuditAsync(
            HttpContext,
            "automation.backup.remote_tested",
            $"backup-target:{request.TargetType}",
            $"Off-site backup target tested: {request.TargetType} '{request.RemoteUrl}' "
            + $"as user '{request.RemoteUserName}' (region '{request.RemoteRegion}'); "
            + $"success: {result.Success} ({result.Message}).");

        return Ok(ApiResponse<BackupRemoteTestResultDto>.Ok(result));
    }
}
