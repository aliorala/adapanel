using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Customer,Reseller")]
[RequireSubUserSection("backup")]
[Route("api/v1/backup")]
public sealed class BackupController(
    IBackupService backupService,
    ICustomerScopeService customerScopes,
    ISubscriptionBackupScheduleService backupSchedules,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<BackupRecordDto>>>> GetBackupsAsync(CancellationToken cancellationToken)
    {
        var backups = await backupService.GetBackupsAsync(cancellationToken);

        // Customer/Reseller yalnızca kendi sitelerinin ve KENDİ ABONELİĞİNİN yedeklerini görür.
        var scope = await CustomerAccess.GetSiteScopeAsync(customerScopes, User, cancellationToken);
        if (scope is not null)
        {
            var abonelikler = await GetAllowedSubscriptionIdsAsync(cancellationToken);
            backups = backups
                .Where(record => IsVisibleToScope(record, scope, abonelikler))
                .Select(HideServerPaths)
                .ToArray();
        }

        return Ok(ApiResponse<IReadOnlyCollection<BackupRecordDto>>.Ok(backups));
    }

    /// <summary>
    /// Kapsamlı kullanıcıya (müşteri/bayi) dönen kayıttan SUNUCU YOLLARINI düşürür.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Path</c> mutlak bir sunucu yoludur (<c>C:\ProgramData\AdaPanel\backups\…</c>). Kiracının
    /// ona ihtiyacı yoktur — dosyayı panelden indirir — ama sunucunun dizin düzenini öğrenmesi,
    /// başka bir açığı hedeflemeyi kolaylaştırır. Kapsam süzgeci hangi KAYITLARI göreceğini
    /// belirliyordu; hangi ALANLARI göreceğini belirlemiyordu.
    /// </para>
    /// <para>
    /// Yönetici/operatör için yol AYNEN kalır: onların işi zaten sunucuyu yönetmek ve panel
    /// "yedek diskte nerede" sorusunu cevaplayabilmeli.
    /// </para>
    /// <para>
    /// <b>DİKKAT — nesne yerinde değiştiriliyor.</b> Bu güvenlidir çünkü
    /// <c>BackupService.GetBackupsAsync</c> kayıtları her çağrıda diskten TAZE deserialize eder
    /// (<c>JsonFileStore.ReadAsync</c>), yani her isteğin kendi nesne kopyası vardır. O katmana
    /// bir gün önbellek eklenirse burası KOPYA ÜRETMELİDİR; yoksa bir kiracının isteği
    /// yöneticinin gördüğü yolları da siler ve bu sessizce olur.
    /// </para>
    /// </remarks>
    private static BackupRecordDto HideServerPaths(BackupRecordDto record)
    {
        record.Path = string.Empty;
        return record;
    }

    /// <summary>
    /// Kapsamlı bir kullanıcı bu yedek kaydını görebilir mi?
    /// </summary>
    /// <remarks>
    /// Site yedeğinde ölçü sitenin kapsamda olmasıdır; ABONELİK yedeğinde ise kaydın
    /// <c>SubscriptionUserId</c> değeridir. İkincisi olmadan bir müşteri, listede başka bir
    /// müşterinin abonelik arşivini görebilir ve indirebilirdi.
    /// </remarks>
    private static bool IsVisibleToScope(
        BackupRecordDto record,
        IReadOnlyCollection<string> siteScope,
        IReadOnlySet<string>? allowedSubscriptionIds)
    {
        if (string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase))
        {
            return siteScope.Contains(record.Source, StringComparer.OrdinalIgnoreCase);
        }

        if (string.Equals(record.Type, "subscription", StringComparison.OrdinalIgnoreCase))
        {
            return allowedSubscriptionIds is not null
                && !string.IsNullOrWhiteSpace(record.SubscriptionUserId)
                && allowedSubscriptionIds.Contains(record.SubscriptionUserId);
        }

        // Sunucu geneli yedekler (panel, posta sunucusu, sahipsiz veritabanları) YALNIZ yöneticide.
        return false;
    }

    /// <summary>
    /// Kullanıcının erişebileceği abonelik kimlikleri; kapsamsız rol (yönetici) için <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Bayi kendi müşterilerinin aboneliklerine erişir (Plesk'te de bayi müşterisinin yedeğini
    /// alır/geri yükler); müşteri yalnız KENDİ aboneliğine. Alt-kullanıcı, bağlı olduğu ana
    /// aboneliğin kimliğini alır — abonelik kaynakları ortak havuzdur.
    /// </remarks>
    private async Task<IReadOnlySet<string>?> GetAllowedSubscriptionIdsAsync(CancellationToken cancellationToken)
    {
        if (!CustomerAccess.IsScoped(User))
        {
            return null;
        }

        var kimlikler = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userId = User.GetRequiredUserId();

        if (CustomerAccess.IsReseller(User))
        {
            foreach (var musteri in await customerScopes.GetOwnedUserIdsAsync(userId, cancellationToken))
            {
                kimlikler.Add(musteri.ToString("N"));
            }
        }

        kimlikler.Add((await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken)).ToString("N"));
        return kimlikler;
    }

    /// <summary>
    /// Bu kullanıcı verilen aboneliğe işlem yapabilir mi (yedek al, program kur, çalıştır).
    /// Yetkisizse 403 fırlatır.
    /// </summary>
    private async Task EnsureSubscriptionAccessAsync(Guid userId, CancellationToken cancellationToken)
    {
        var izinliler = await GetAllowedSubscriptionIdsAsync(cancellationToken);
        if (izinliler is null)
        {
            return; // yönetici
        }

        if (!izinliler.Contains(userId.ToString("N")))
        {
            throw new AdaPanel.Core.Common.AppException("auth.subscription_forbidden", 403, 403);
        }
    }

    /// <summary>
    /// Paneldeki varsayılan yedek şifreleme ayarı. Parola ASLA dönmez; yalnızca açık mı ve
    /// kayıtlı parola gerçekten kullanılabilir mi (çözülebiliyor mu) bilgisi döner.
    /// </summary>
    [HttpGet("encryption")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupEncryptionSettingsDto>>> GetEncryptionAsync(CancellationToken cancellationToken)
    {
        var settings = await backupService.GetEncryptionSettingsAsync(cancellationToken);
        return Ok(ApiResponse<BackupEncryptionSettingsDto>.Ok(settings));
    }

    /// <summary>
    /// Varsayılan yedek şifreleme parolasını belirler (boş parola = şifrelemeyi kapat).
    /// <para>
    /// PAROLA KAYBOLURSA ŞİFRELİ YEDEKLER GERİ GETİRİLEMEZ; panelin kurtarma kapısı yoktur.
    /// Bu yüzden işlem denetim günlüğüne yazılır (parola YAZILMAZ).
    /// </para>
    /// </summary>
    [HttpPut("encryption")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupEncryptionSettingsDto>>> SetEncryptionAsync(
        [FromBody] SetBackupEncryptionRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await backupService.SetEncryptionSettingsAsync(request, cancellationToken);

        await auditService.WriteAsync(
            "backup.encryption_changed",
            "backup",
            settings.Enabled
                ? "Backup encryption password set (AES-256-GCM). Losing the password makes encrypted backups unrecoverable."
                : "Backup encryption disabled; new backups are stored unencrypted.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<BackupEncryptionSettingsDto>.Ok(settings, "backup.encryption_saved"));
    }

    [HttpPost("site")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupSiteAsync([FromBody] BackupSiteRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);
        var backup = await backupService.BackupSiteAsync(request, cancellationToken);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    /// <summary>Yerel yedek dosyasını .zip olarak indirir (profesyonel panel benzeri "indir").</summary>
    [HttpGet("{backupId}/download")]
    public async Task<IActionResult> DownloadAsync(string backupId, CancellationToken cancellationToken)
    {
        var backups = await backupService.GetBackupsAsync(cancellationToken);
        var record = backups.FirstOrDefault(item => string.Equals(item.Id, backupId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return NotFound(ApiResponse<object>.Fail("backup.not_found"));
        }

        // Customer/Reseller yalnızca kendi sitelerinin ve kendi aboneliğinin yedeğini indirebilir.
        var scope = await CustomerAccess.GetSiteScopeAsync(customerScopes, User, cancellationToken);
        if (scope is not null &&
            !IsVisibleToScope(record, scope, await GetAllowedSubscriptionIdsAsync(cancellationToken)))
        {
            return NotFound(ApiResponse<object>.Fail("backup.not_found"));
        }

        if (string.IsNullOrWhiteSpace(record.Path) || !System.IO.File.Exists(record.Path))
        {
            return NotFound(ApiResponse<object>.Fail("backup.not_found"));
        }

        var contentType = record.Format.Equals("zip", StringComparison.OrdinalIgnoreCase)
            ? "application/zip"
            : "application/octet-stream";
        return PhysicalFile(record.Path, contentType, record.Name);
    }

    [HttpPost("db")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupDatabaseAsync([FromBody] BackupDatabaseRequest request, CancellationToken cancellationToken)
    {
        var backup = await backupService.BackupDatabaseAsync(request, cancellationToken);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    /// <summary>Mail (Stalwart) verisini yedekler; mail servisi kısa süreliğine durdurulur.</summary>
    [HttpPost("mail")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupMailAsync([FromBody] BackupMailRequest request)
    {
        // Servisi durdurup başlatan akış istemci iptaline bağlanmaz.
        var backup = await backupService.BackupMailAsync(request, CancellationToken.None);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    /// <summary>Panelin kendi durumunu yedekler: adapanel.db + config (DataProtection anahtarları hariç).</summary>
    [HttpPost("panel")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupPanelAsync([FromBody] BackupPanelRequest? request, CancellationToken cancellationToken)
    {
        var backup = await backupService.BackupPanelAsync(request ?? new BackupPanelRequest(), cancellationToken);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    // Tam geri yükleme (hedef yol seçimi dahil) yalnız yöneticidedir; müşteriler
    // kendi sitelerine "restore-items" ile granüler geri yükleme yapar.
    [HttpPost("restore")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> RestoreAsync([FromBody] RestoreBackupRequest request, CancellationToken cancellationToken)
    {
        var backup = await backupService.RestoreAsync(request, cancellationToken);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.restored"));
    }

    /// <summary>
    /// "Yedekleri doğrula": kayıtların GERÇEKTEN durup durmadığını ölçer ve hedefte duran ama
    /// kaydı olmayan (yetim) dosyaları raporlar. HİÇBİR ŞEYİ SİLMEZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden düğmeye bağlı:</b> uzak ölçüm kayıt başına bir ağ isteğidir. Listelemeye
    /// bağlanması paneli her açılışta yavaşlatır ve hedefin hız sınırına takardı; ucuz olan
    /// yerel ölçüm zaten her listelemede yapılıyor.
    /// </para>
    /// <para>
    /// <b>Neden yalnız yönetici:</b> ölçüm sunucu genelindeki BÜTÜN kayıtları tarar ve hedefteki
    /// dosya adlarını (başka kiracıların yedekleri dâhil) döndürür. Kapsamlı kullanıcıya açmak,
    /// kiracı izolasyonunu deler.
    /// </para>
    /// </remarks>
    [HttpPost("verify")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupVerificationResultDto>>> VerifyAsync(CancellationToken cancellationToken)
    {
        var result = await backupService.VerifyBackupsAsync(cancellationToken);

        await auditService.WriteAsync(
            "backup.verified",
            "backup",
            $"Backup verification: {result.Checked} checked, {result.MissingLocal} missing locally, "
            + $"{result.MissingRemote} missing remotely, {result.Unreachable} unreachable, "
            + $"{result.Orphans.Count} orphan file(s).",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<BackupVerificationResultDto>.Ok(result, "backup.verified"));
    }

    /// <summary>Zip yedeğinin içeriğine dizin bazlı gözatma (granüler geri yükleme).</summary>
    [HttpGet("{backupId}/entries")]
    public async Task<ActionResult<ApiResponse<BackupEntriesDto>>> GetEntriesAsync(
        string backupId,
        [FromQuery] string? prefix,
        CancellationToken cancellationToken)
    {
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);
        var entries = await backupService.GetEntriesAsync(backupId, prefix, cancellationToken);
        return Ok(ApiResponse<BackupEntriesDto>.Ok(entries));
    }

    [HttpPost("restore-items")]
    public async Task<ActionResult<ApiResponse<RestoreBackupItemsResultDto>>> RestoreItemsAsync(
        [FromBody] RestoreBackupItemsRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureBackupOwnershipAsync(request.BackupId, cancellationToken);

        // GÜVENLİK (çapraz-kiracı): yönetici DIŞINDA kimse hedef yol seçemez; içerik her zaman
        // sitenin kendi konumuna yüklenir.
        //
        // Burada önceden yalnız `IsCustomer` bakılıyordu, yani BAYİ dışarıda kalıyordu. Oysa bayi
        // de bir kiracıdır: serbest hedef yolla bu uç, yedeğin içindeki dosyaları diskte istenen
        // yere yazan bir araca dönüşüyordu — panel LocalSystem olarak çalıştığı için başka
        // müşterilerin site klasörleri de, Windows'un kendi klasörleri de dahil.
        //
        // Tam geri yükleme (`POST restore`) zaten yönetici-özel; kapsamlı kullanıcıya açık olan
        // tek yol burasıydı.
        if (CustomerAccess.IsScoped(User))
        {
            request.TargetPath = string.Empty;
        }

        var result = await backupService.RestoreItemsAsync(request, cancellationToken);
        return Ok(ApiResponse<RestoreBackupItemsResultDto>.Ok(result, "backup.items_restored"));
    }

    /// <summary>
    /// Yedeği siler: UZAK kopya + yerel dosya + kayıt. Admin her yedeği, müşteri/bayi yalnız
    /// etkin kapsamındaki SITE yedeklerini silebilir.
    /// </summary>
    /// <remarks>
    /// Uzak kopya silinemezse istek <c>backup.remote_delete_failed</c> ile biter ve KAYIT
    /// KORUNUR: kaydı silmek, uzak dosyayı panelin bir daha göremeyeceği bir yetime çevirirdi.
    /// </remarks>
    [HttpDelete("{backupId}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAsync(string backupId, CancellationToken cancellationToken)
    {
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);

        var record = (await backupService.GetBackupsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, backupId, StringComparison.OrdinalIgnoreCase));

        await backupService.DeleteBackupAsync(backupId, cancellationToken);

        await auditService.WriteAsync(
            "backup.deleted",
            "backup",
            $"Backup '{record?.Name ?? backupId}' ({record?.Type}/{record?.Source}) deleted.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { id = backupId }, "backup.deleted"));
    }

    /// <summary>
    /// Customer/Reseller için yedek kaydının etkin kapsamında olduğunu doğrular: SİTE yedeğinde
    /// site kapsamda olmalı, ABONELİK yedeğinde abonelik erişilebilir olmalı.
    /// </summary>
    private async Task EnsureBackupOwnershipAsync(string backupId, CancellationToken cancellationToken)
    {
        if (!CustomerAccess.IsScoped(User))
        {
            return;
        }

        var record = (await backupService.GetBackupsAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == backupId)
            ?? throw new AdaPanel.Core.Common.AppException("backup.not_found", 404, 404);

        if (string.Equals(record.Type, "subscription", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParseExact(record.SubscriptionUserId, "N", out var sahip))
            {
                throw new AdaPanel.Core.Common.AppException("auth.subscription_forbidden", 403, 403);
            }

            await EnsureSubscriptionAccessAsync(sahip, cancellationToken);
            return;
        }

        if (!string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase))
        {
            throw new AdaPanel.Core.Common.AppException("auth.site_forbidden", 403, 403);
        }

        await CustomerAccess.EnsureSiteAsync(customerScopes, User, record.Source, cancellationToken);
    }

    // =====================================================================================
    // ABONELİK YEDEĞİ + ZİNCİR + ABONELİK BAŞINA PROGRAM
    // =====================================================================================

    /// <summary>
    /// Abonelik bazında yedek alır: müşterinin bütün siteleri + veritabanları + posta alan adları +
    /// FTP hesapları tek arşivde.
    /// </summary>
    [HttpPost("subscription")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupSubscriptionAsync(
        [FromBody] BackupSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        // KİRACI İZOLASYONU: kapsamlı kullanıcı gövdeye BAŞKA bir abonelik kimliği yazamaz.
        // Kimlik istekten okunup doğrulanmasaydı, bir müşteri başka bir müşterinin bütün
        // verisini içeren bir arşiv ürettirip indirebilirdi.
        await EnsureSubscriptionAccessAsync(request.UserId, cancellationToken);

        var backup = await backupService.BackupSubscriptionAsync(request, cancellationToken);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    /// <summary>
    /// Yedeğin artımsal ZİNCİRİ: tam yedekten hedefe sıralı halkalar + kopuk mu bilgisi.
    /// Ekran, geri yüklemeyi denemeden önce eksik halkaları gösterebilsin diye.
    /// </summary>
    [HttpGet("{backupId}/chain")]
    public async Task<ActionResult<ApiResponse<BackupChainDto>>> GetChainAsync(string backupId, CancellationToken cancellationToken)
    {
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);
        var chain = await backupService.GetChainAsync(backupId, cancellationToken);
        return Ok(ApiResponse<BackupChainDto>.Ok(chain));
    }

    /// <summary>
    /// Yedeği VE ona bağımlı bütün artımsal halkaları siler.
    /// <para>
    /// Tek yedek silme (<c>DELETE {backupId}</c>) bağımlı halka varsa reddedilir; kullanıcı neyi
    /// kaybedeceğini gördükten sonra bu ucu bilerek çağırır.
    /// </para>
    /// </summary>
    [HttpDelete("{backupId}/chain")]
    public async Task<ActionResult<ApiResponse<BackupDeleteResultDto>>> DeleteChainAsync(string backupId, CancellationToken cancellationToken)
    {
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);
        var result = await backupService.DeleteBackupChainAsync(backupId, cancellationToken);

        await auditService.WriteAsync(
            "backup.chain_deleted",
            "backup",
            $"Backup chain deleted ({result.DeletedIds.Length} link(s)): {string.Join(", ", result.DeletedNames)}",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<BackupDeleteResultDto>.Ok(result, "backup.chain_deleted"));
    }

    /// <summary>Tanımlı bütün abonelik yedek programları (kapsamlı kullanıcıda kendi/aitleri).</summary>
    [HttpGet("schedules")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SubscriptionBackupScheduleDto>>>> GetSchedulesAsync(CancellationToken cancellationToken)
    {
        var schedules = await backupSchedules.GetSchedulesAsync(cancellationToken);

        var izinliler = await GetAllowedSubscriptionIdsAsync(cancellationToken);
        if (izinliler is not null)
        {
            schedules = schedules
                .Where(item => izinliler.Contains(item.UserId.ToString("N")))
                .ToArray();
        }

        return Ok(ApiResponse<IReadOnlyCollection<SubscriptionBackupScheduleDto>>.Ok(schedules));
    }

    [HttpGet("schedules/{userId:guid}")]
    public async Task<ActionResult<ApiResponse<SubscriptionBackupScheduleDto>>> GetScheduleAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureSubscriptionAccessAsync(userId, cancellationToken);
        var schedule = await backupSchedules.GetScheduleAsync(userId, cancellationToken);
        return Ok(ApiResponse<SubscriptionBackupScheduleDto>.Ok(schedule));
    }

    /// <summary>
    /// Abonelik yedek programını kaydeder. İstenen sıklık planın günlük kotasını aşıyorsa sessizce
    /// kabul edilmez: kotaya çekilir ve dönen kayıtta <c>throttledByQuota</c> ile bildirilir.
    /// </summary>
    [HttpPut("schedules/{userId:guid}")]
    public async Task<ActionResult<ApiResponse<SubscriptionBackupScheduleDto>>> UpdateScheduleAsync(
        Guid userId,
        [FromBody] UpdateSubscriptionBackupScheduleRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureSubscriptionAccessAsync(userId, cancellationToken);
        var schedule = await backupSchedules.UpdateScheduleAsync(userId, request, cancellationToken);

        await auditService.WriteAsync(
            "backup.schedule_changed",
            "backup",
            $"Subscription backup schedule updated for {userId:N}: enabled={schedule.Enabled}, " +
            $"every {schedule.IntervalHours}h, keep {schedule.RetentionDays} day(s)/{schedule.KeepCount} copy, " +
            $"incremental={schedule.Incremental}, throttled={schedule.ThrottledByQuota}.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<SubscriptionBackupScheduleDto>.Ok(schedule, "backup.schedule_saved"));
    }

    [HttpDelete("schedules/{userId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteScheduleAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureSubscriptionAccessAsync(userId, cancellationToken);
        await backupSchedules.RemoveScheduleAsync(userId, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { userId }, "backup.schedule_deleted"));
    }

    /// <summary>
    /// Programı beklemeden şimdi çalıştırır. Günlük kota bu yolu ENGELLEMEZ: müşteri riskli bir
    /// işlemden önce yedek almak istediğinde onu durdurmak, korumanın kendisini durdurmak olurdu.
    /// </summary>
    [HttpPost("schedules/{userId:guid}/run")]
    public async Task<ActionResult<ApiResponse<SubscriptionBackupScheduleDto>>> RunScheduleNowAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureSubscriptionAccessAsync(userId, cancellationToken);
        var schedule = await backupSchedules.RunNowAsync(userId, cancellationToken);
        return Ok(ApiResponse<SubscriptionBackupScheduleDto>.Ok(schedule, "backup.schedule_run"));
    }
}
