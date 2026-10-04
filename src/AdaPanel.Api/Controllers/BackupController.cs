using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Customer,Reseller")]
[RequireSubUserSection("backup")]
[Route("api/v1/backup")]
public sealed class BackupController(
    IBackupService backupService,
    ICustomerScopeService customerScopes,
    IDatabaseOwnershipService dbOwnership,
    ISubscriptionBackupScheduleService backupSchedules,
    IRunningJobService runningJobs,
    IAuditService auditService,
    AdaPanelDbContext dbContext) : ControllerBase
{
    // =====================================================================================
    // SÜREN İŞİN SUNUCUDAN GERİ OKUNMASI
    //
    // ÖLÇÜLEN BOŞLUK: yedek ve geri yükleme uçları işi ARKA PLANA ALMAZ — HTTP isteği iş bitene
    // kadar açık kalır. Kullanıcı sayfayı yenilediğinde ya da sekmeyi kapattığında iş sunucuda
    // DEVAM eder ama panelde "sürüyor" bilgisi tamamen kaybolurdu: ekran boş görünür, kullanıcı
    // yedeği ikinci kez tetiklerdi. Antivirüs taramasında aynı hata 0.6.6'da kapatıldı; burada
    // AYNI desen uygulanıyor (bkz. Infrastructure/RunningJobs/RunningJobService.cs).
    // =====================================================================================

    /// <summary>
    /// Bu sunucuda ŞU AN süren yedekleme/geri yükleme işleri + panel yeniden başladığı için
    /// yarıda kalmış olanlar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden yüzde yok:</b> yedekleme toplam dosya sayısını sıkıştırmaya başlamadan bilmez;
    /// yüzde göstermenin tek yolu onu uydurmak olurdu. Verilen iki şey ÖLÇÜLEBİLİR: işin konusu
    /// ve sunucudaki başlangıç anı.
    /// </para>
    /// <para>
    /// <b>Kiracı izolasyonu:</b> kapsamlı kullanıcı yalnız KENDİ aboneliğine ait işleri görür.
    /// Sunucu geneli işler (panel yedeği, posta sunucusu yedeği) sahipsizdir ve yalnız yöneticide
    /// görünür — aksi hâlde bir müşteri, başka bir müşterinin sitesinin adını şeritten okurdu.
    /// </para>
    /// </remarks>
    [HttpGet("running")]
    public async Task<ActionResult<ApiResponse<RunningJobsStatusDto>>> GetRunningJobsAsync(CancellationToken cancellationToken)
    {
        var status = await runningJobs.GetStatusAsync(
            [RunningJobKinds.Backup, RunningJobKinds.Restore],
            await GetAllowedSubscriptionIdsAsync(cancellationToken),
            cancellationToken);

        return Ok(ApiResponse<RunningJobsStatusDto>.Ok(status));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<BackupRecordDto>>>> GetBackupsAsync(CancellationToken cancellationToken)
    {
        var authority = await CaptureReadAuthorityAsync(cancellationToken);
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

        await RevalidateReadAuthorityAsync(authority, cancellationToken);
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
    /// Yanıt ayrı bir kopyadır: oluşturma sonucunu veya servis tarafından paylaşılan bir
    /// kaydı arındırmak, diskteki metadata ya da yöneticinin daha sonra okuyacağı nesneyi
    /// değiştirmemelidir. Uyarının anahtarı ve mantıksal hedefi kalır; teknik ayrıntısı
    /// sunucu yolları ve servis kimlik bilgileri taşıyabildiğinden yalnız yöneticiye döner.
    /// </para>
    /// </remarks>
    private static BackupRecordDto HideServerPaths(BackupRecordDto record)
    {
        return new BackupRecordDto
        {
            Id = record.Id,
            Name = record.Name,
            Type = record.Type,
            Source = record.Source,
            Format = record.Format,
            CreatedAtUtc = record.CreatedAtUtc,
            Size = record.Size,
            Status = record.Status,
            TargetType = record.TargetType,
            RemoteUri = SafeRemoteLocation(record.RemoteUri),
            Encrypted = record.Encrypted,
            Compressed = record.Compressed,
            Warnings = record.Warnings.Select(warning => new BackupRestoreWarningDto
            {
                MessageKey = warning.MessageKey,
                Target = SafeWarningTarget(warning.Target)
            }).ToList(),
            IncludedDatabases = record.IncludedDatabases?.Select(database => new BackupDatabaseRef
            {
                Engine = database.Engine, DatabaseName = database.DatabaseName
            }).ToList(),
            IncludedComponents = record.IncludedComponents?.Select(component => new BackupComponentRef
            {
                Kind = component.Kind, Source = component.Source
            }).ToList(),
            LocalMissing = record.LocalMissing,
            RemoteMissing = record.RemoteMissing,
            RemoteVerifiedUtc = record.RemoteVerifiedUtc,
            RemoteState = record.RemoteState,
            OffsiteCopyLost = record.OffsiteCopyLost,
            AllCopiesMissing = record.AllCopiesMissing,
            IntegrityState = record.IntegrityState,
            ContentHash = record.ContentHash,
            RemoteDeleteFailed = record.RemoteDeleteFailed,
            ChainId = record.ChainId,
            ChainIndex = record.ChainIndex,
            BackupMode = record.BackupMode,
            BaseBackupId = record.BaseBackupId,
            SubscriptionUserId = record.SubscriptionUserId,
            ItemCount = record.ItemCount
        };
    }

    private BackupRecordDto ProjectBackupRecord(BackupRecordDto record) =>
        CustomerAccess.IsScoped(User) ? HideServerPaths(record) : record;

    private static string SafeRemoteLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        // FTP's Uri parser can treat a literal '?' as part of the path and escape it.
        // Remove raw query/fragment delimiters before parsing; encoded filename characters
        // such as %3F and %23 remain part of the real remote object name.
        var location = value.Trim();
        var suffix = location.IndexOfAny(['?', '#']);
        if (suffix >= 0) location = location[..suffix];
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.IsFile)
            return string.Empty;
        // OAuth destinations identify a folder inside the account, e.g. dropbox:/backups/a.zip.
        // Keep that remote-copy evidence while still rejecting hostless arbitrary text.
        var logicalCloudPath = (uri.Scheme is "dropbox" or "onedrive") && uri.AbsolutePath.StartsWith('/');
        if (string.IsNullOrEmpty(uri.Host) && !logicalCloudPath)
            return string.Empty;

        return new UriBuilder(uri)
        {
            UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty
        }.Uri.AbsoluteUri;
    }

    private static string SafeWarningTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var target = value.Trim();
        var absoluteUri = Uri.TryCreate(target, UriKind.Absolute, out var uri);
        if (Path.IsPathRooted(target) || (absoluteUri && uri!.IsFile))
            return string.Empty;
        return target.Contains("://", StringComparison.Ordinal) || (absoluteUri && uri!.Scheme is "dropbox" or "onedrive")
            ? SafeRemoteLocation(target) : value;
    }

    /// <summary>
    /// Kapsamlı bir kullanıcı bu yedek kaydını görebilir mi?
    /// </summary>
    /// <remarks>
    /// Site yedeği hem güncel site kapsamını hem arşiv alınırken ölçülen abonelik sahibini
    /// gerektirir. Sahibi bilinmeyen eski/karma arşivler yalnız yöneticiye açıktır.
    /// </remarks>
    private static bool IsVisibleToScope(
        BackupRecordDto record,
        IReadOnlyCollection<string> siteScope,
        IReadOnlySet<string>? allowedSubscriptionIds)
    {
        if (string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase))
        {
            return siteScope.Contains(record.Source, StringComparer.OrdinalIgnoreCase)
                && allowedSubscriptionIds is not null
                && !string.IsNullOrWhiteSpace(record.SubscriptionUserId)
                && allowedSubscriptionIds.Contains(record.SubscriptionUserId);
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

    // =====================================================================================
    // DENETİM İZİ (bu denetleyicinin ORTAK yolu)
    //
    // ÖLÇÜLEN BOŞLUK: yedek OLUŞTURMA ve GERİ YÜKLEME uçlarının hiçbiri iz bırakmıyordu.
    // Geri yükleme, panelin CANLI SİTE VERİSİNİN ÜZERİNE YAZAN işlemidir; "sitem dün geceki
    // hâline dönmüş, bunu kim yaptı?" sorusunun cevabı hiçbir yerde yoktu. Şifreleme ayarı,
    // silme ve doğrulama zaten yazıyordu — asıl yıkıcı iki uç yazmıyordu.
    //
    // Kayıtlar WriteForSubscriptionAsync ile yazılır: eylemi YAPAN (yönetici/bayi olabilir) ile
    // eylemin ÜZERİNDE yapıldığı kaynağın SAHİBİ ayrı sütunlardır. İkincisi olmadan kiracı,
    // kendi sitesine yapılan geri yüklemeyi kendi denetim ekranında GÖREMEZ.
    // =====================================================================================

    /// <summary>Yedek işleminin izini yazar (yapan = oturumdaki kullanıcı, konu = kaynağın sahibi).</summary>
    private Task WriteBackupAuditAsync(
        string action,
        string resource,
        string details,
        Guid? subjectUserId,
        CancellationToken cancellationToken) =>
        auditService.WriteForSubscriptionAsync(
            action,
            resource,
            details,
            User.GetRequiredUserId(),
            subjectUserId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

    /// <summary>
    /// Bir sitenin sahibi (kota/abonelik sahibi). Okunamazsa <c>null</c> — kaydı hiç yazmamaktansa
    /// konusu boş yazmak yeğdir.
    /// </summary>
    private async Task<Guid?> ResolveSiteSubjectAsync(string siteName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(siteName))
        {
            return null;
        }

        try
        {
            return await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sahiplik dosyası okunamadı: kayıt yine yazılır, yalnızca KONUSU boş kalır.
            // İptal YUTULMAZ — o bir hata değil, isteğin sonlandırılmasıdır.
            return null;
        }
    }

    /// <summary>Arşivin doğrulanmış kalıcı sahibi; bilinmeyen sahip güncel site atamasından türetilmez.</summary>
    private static Task<Guid?> ResolveRecordSubjectAsync(BackupRecordDto? record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Guid?>(record is not null &&
            Guid.TryParseExact(record.SubscriptionUserId, "N", out var owner) && owner != Guid.Empty ? owner : null);
    }

    /// <summary>
    /// Oluşturulan yedeğin izi. DURUM da yazılır: <c>created_with_errors</c> ile <c>created</c>
    /// arasındaki fark tam olarak "yedek eksik mi" sorusunun cevabıdır ve denetim kaydından da
    /// okunabilmelidir.
    /// </summary>
    private Task WriteBackupCreatedAuditAsync(BackupRecordDto backup, Guid? subjectUserId, CancellationToken cancellationToken) =>
        WriteBackupAuditAsync(
            "backup.created",
            backup.Name,
            $"Backup created: {backup.Type}/{backup.Source} -> '{backup.Name}' "
            + $"(status={backup.Status}, target={backup.TargetType}, encrypted={backup.Encrypted}, "
            + $"warnings={backup.Warnings.Count}).",
            subjectUserId,
            cancellationToken);

    /// <summary>
    /// Tek bir sitenin yedeği. Kapsam denetimi HEM siteye HEM gövdedeki veritabanı listesine uygulanır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN AÇIK (çapraz-kiracı veri sızdırma):</b> burada yalnız <c>request.SiteName</c>
    /// denetleniyordu. <c>request.Databases</c> ise gövdeden geliyor ve
    /// <c>BackupService</c> onu doğrudan <c>ExportDumpAsync</c>'e veriyor — döküm panelin
    /// KÖK/yönetici kimliğiyle alınır, yani her veritabanı erişilebilir. Kiracı KENDİ sitesinin
    /// adını, gövdede ise BAŞKASININ veritabanını verip arşivi
    /// <c>GET /api/v1/backup/{'{'}id{'}'}/download</c> ile indirebiliyordu; indirme denetimi arşivi
    /// kendi sitesinin yedeği gördüğü için geçiyordu. Dosya izolasyonundaki (0.8.8) sınıfın
    /// veritabanı hâli: <b>kapsam denetimi doğru, denetlenen değeri kiracı seçiyor.</b>
    /// </para>
    /// <para>
    /// Denetim <see cref="CustomerAccess.EnsureDatabaseAsync"/> ile yapılır; site↔veritabanı
    /// bağlama ucuyla aynı aktör kapsamını kullanır. Paylaşılacak site arşivi ayrıca sitenin
    /// ölçülen sahibini ve her veritabanının açık sahiplik kaydını doğrular; ad benzerliği
    /// arşiv sahipliği kanıtı değildir. Yönetici karma arşiv alabilir, yalnız kendisi görür.
    /// </para>
    /// </remarks>
    [HttpPost("site")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupSiteAsync([FromBody] BackupSiteRequest request, CancellationToken cancellationToken)
    {
        var actor = await EnterActorAsync(cancellationToken);
        using var actorLease = actor.Lease;
        async Task BeforeBackup(CancellationToken token)
        {
            await actor.Authority.RevalidateAsync(dbContext, customerScopes, User, token);
            await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, token);
            foreach (var database in request.Databases ?? [])
                await CustomerAccess.EnsureDatabaseAsync(
                    customerScopes, dbOwnership, User, database.Engine, database.DatabaseName, token);
            if (CustomerAccess.IsScoped(User))
            {
                var owner = await customerScopes.GetSiteOwnerUserIdAsync(request.SiteName, token);
                if (owner is null || owner == Guid.Empty)
                    throw new AdaPanel.Core.Common.AppException("backup.ownership_unconfirmed", 409, 409);
                await EnsureSubscriptionAccessAsync(owner.Value, token);
                // A reseller may manage both customers, but that does not authorize
                // sharing one customer's dump inside the other customer's site archive.
                var databases = request.Databases ?? [];
                if (databases.Count > 0)
                {
                    var ownership = await dbOwnership.GetAllAsync(token);
                    foreach (var database in databases)
                    {
                        var matches = ownership.Where(record => AdaPanel.Core.Common.DatabaseResourceIdentity.Same(
                            record.Engine, record.DatabaseName, database.Engine, database.DatabaseName)).ToArray();
                        if (matches.Length == 0 || matches.Any(record => record.OwnerUserId != owner))
                            throw new AdaPanel.Core.Common.AppException("backup.ownership_unconfirmed", 409, 409);
                    }
                }
            }
        }
        await BeforeBackup(cancellationToken);

        // Çalışan iş sitenin mevcut aboneliğine aittir. Son denetim kaydı ise arşivin
        // kalıcı sahibini kullanır: yönetici karma içerik seçtiyse yalnız yöneticiye görünür.
        var konu = await ResolveSiteSubjectAsync(request.SiteName, cancellationToken);
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Backup, request.SiteName, konu, cancellationToken);

        var backup = await backupService.BackupSiteAsync(request, BeforeBackup, cancellationToken);

        await WriteBackupCreatedAuditAsync(backup, await ResolveRecordSubjectAsync(backup, cancellationToken), cancellationToken);

        return Ok(ApiResponse<BackupRecordDto>.Ok(ProjectBackupRecord(backup), "backup.created"));
    }

    /// <summary>Yerel yedek dosyasını .zip olarak indirir (profesyonel panel benzeri "indir").</summary>
    [HttpGet("{backupId}/download")]
    public async Task<IActionResult> DownloadAsync(string backupId, CancellationToken cancellationToken)
    {
        var authority = await CaptureReadAuthorityAsync(cancellationToken);
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

        await RevalidateReadAuthorityAsync(authority, cancellationToken);

        if (string.IsNullOrWhiteSpace(record.Path) || !System.IO.File.Exists(record.Path))
        {
            return NotFound(ApiResponse<object>.Fail("backup.not_found"));
        }

        var contentType = record.Format.Equals("zip", StringComparison.OrdinalIgnoreCase)
            ? "application/zip"
            : "application/octet-stream";
        return PhysicalFile(record.Path, contentType, record.Name);
    }

    [HttpPost("{backupId}/retry-remote")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> RetryRemoteAsync(string backupId,
        [FromBody] RetryBackupRemoteRequest request, CancellationToken cancellationToken)
    {
        var actor = await EnterActorAsync(cancellationToken, administratorOnly: true);
        using var actorLease = actor.Lease;
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Backup, backupId, subjectUserId: null, cancellationToken);
        var backup = await backupService.RetryRemoteUploadAsync(backupId, request.RemoteUrl,
            (record, token) => RevalidateMutationAsync(actor.Authority, record, token), cancellationToken);
        await WriteBackupAuditAsync("backup.remote_retried", backup.Name,
            $"Remote upload completed: {backup.RemoteUri}",
            await ResolveRecordSubjectAsync(backup, CancellationToken.None), CancellationToken.None);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.remote_retried"));
    }

    [HttpPost("db")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupDatabaseAsync([FromBody] BackupDatabaseRequest request, CancellationToken cancellationToken)
    {
        var actor = await EnterActorAsync(cancellationToken, administratorOnly: true);
        using var actorLease = actor.Lease;
        // Konu YOK: sunucu geneli/yönetici işlemi (veritabanı sahipliği yedek kaydında taşınmaz).
        // Aynı sebeple çalışan-iş kaydı da sahipsizdir → yalnız yöneticide görünür.
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Backup,
            $"{request.Engine}/{request.DatabaseName}",
            subjectUserId: null,
            cancellationToken);

        var backup = await backupService.BackupDatabaseAsync(request,
            token => actor.Authority.RevalidateAsync(dbContext, customerScopes, User, token), cancellationToken);
        await WriteBackupCreatedAuditAsync(backup, null, cancellationToken);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    /// <summary>Mail (Stalwart) verisini yedekler; mail servisi kısa süreliğine durdurulur.</summary>
    [HttpPost("mail")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupMailAsync([FromBody] BackupMailRequest request)
    {
        var actor = await EnterActorAsync(CancellationToken.None, administratorOnly: true);
        using var actorLease = actor.Lease;
        // Servisi durdurup başlatan akış istemci iptaline bağlanmaz.
        //
        // ÇALIŞAN-İŞ KAYDI BURADA ÖZELLİKLE DEĞERLİ: bu iş sırasında posta sunucusu DURUYOR.
        // Kullanıcı sayfayı yenileyip "sürüyor" bilgisini kaybettiğinde, dışarıdan görünen posta
        // kesintisinin sebebini panelde hiçbir yerde göremiyordu.
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Backup, "mail", subjectUserId: null, CancellationToken.None);

        var backup = await backupService.BackupMailAsync(request,
            token => actor.Authority.RevalidateAsync(dbContext, customerScopes, User, token), CancellationToken.None);

        // MAIL SUNUCUSU KISA SÜRELİĞİNE DURUYOR: bu, dışarıdan "posta kesintisi" olarak görülen
        // bir olaydır ve izi olmadan sebebi hiç bulunamaz.
        await WriteBackupCreatedAuditAsync(backup, null, CancellationToken.None);

        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    /// <summary>Panelin kendi durumunu yedekler: adapanel.db + config (DataProtection anahtarları hariç).</summary>
    [HttpPost("panel")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupPanelAsync([FromBody] BackupPanelRequest? request, CancellationToken cancellationToken)
    {
        var actor = await EnterActorAsync(cancellationToken, administratorOnly: true);
        using var actorLease = actor.Lease;
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Backup, "panel", subjectUserId: null, cancellationToken);

        var backup = await backupService.BackupPanelAsync(request ?? new BackupPanelRequest(),
            token => actor.Authority.RevalidateAsync(dbContext, customerScopes, User, token), cancellationToken);
        // Panel yedeği ADMIN PAROLA ÖZETLERİNİ ve 2FA gizlerini içerir; kimin ne zaman aldığı
        // izlenebilir olmalı (indirilebilen bir arşiv üretiliyor).
        await WriteBackupCreatedAuditAsync(backup, null, cancellationToken);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    // Tam geri yükleme (hedef yol seçimi dahil) yalnız yöneticidedir; müşteriler
    // kendi sitelerine "restore-items" ile granüler geri yükleme yapar.
    [HttpPost("restore")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> RestoreAsync([FromBody] RestoreBackupRequest request, CancellationToken cancellationToken)
    {
        var actor = await EnterActorAsync(cancellationToken, administratorOnly: true);
        using var actorLease = actor.Lease;
        // Kayıt İŞLEMDEN ÖNCE okunur: geri yükleme yarıda patlarsa (dosyalar canlı kökün üzerine
        // ZATEN yazılmış olabilir) hangi yedekten ne denendiği yine de yazılabilsin.
        var hedef = (await backupService.GetBackupsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, request.BackupId, StringComparison.OrdinalIgnoreCase));
        var konu = await ResolveRecordSubjectAsync(hedef, cancellationToken);
        var tanim = $"{hedef?.Type}/{hedef?.Source} ('{hedef?.Name ?? request.BackupId}')";

        // GERİ YÜKLEME AYRI BİR TÜRDÜR ("restore"), yedekle aynı yuvayı paylaşmaz: ikisi farklı
        // işlerdir ve bir geri yükleme sürerken zamanlanmış bir yedeğin elle tetiklenmesi
        // yasaklanacak bir şey değildir. Aynı anda İKİ geri yükleme ise yasaktır — ikisi de canlı
        // veriye yazar.
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Restore, hedef?.Name ?? request.BackupId, konu, cancellationToken);

        BackupRecordDto backup;
        try
        {
            backup = await backupService.RestoreAsync(request,
                (record, token) => RevalidateMutationAsync(actor.Authority, record, token), cancellationToken);
        }
        catch (Exception exception)
        {
            // BAŞARISIZ geri yükleme de İZ BIRAKIR: canlı veriye dokunulan an burasıdır ve
            // "denendi ama patladı" bilgisi, hiç denenmemiş olmakla asla karıştırılmamalı.
            await WriteBackupAuditAsync(
                "backup.restore_failed",
                hedef?.Name ?? request.BackupId,
                $"Restore FAILED from {tanim}"
                + $"{(string.IsNullOrWhiteSpace(request.TargetPath) ? string.Empty : $" to '{request.TargetPath}'")}: {exception.Message}",
                konu,
                CancellationToken.None);
            throw;
        }

        await WriteBackupAuditAsync(
            "backup.restored",
            backup.Name,
            $"Restored from {tanim}"
            + $"{(string.IsNullOrWhiteSpace(request.TargetPath) ? string.Empty : $" to '{request.TargetPath}'")} "
            + $"(status={backup.Status}, warnings={backup.Warnings.Count}, preserved='{backup.PreservedPath}').",
            konu,
            cancellationToken);

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
        var authority = await CaptureReadAuthorityAsync(cancellationToken);
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);
        var entries = await backupService.GetEntriesAsync(backupId, prefix, cancellationToken);
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);
        await RevalidateReadAuthorityAsync(authority, cancellationToken);
        return Ok(ApiResponse<BackupEntriesDto>.Ok(entries));
    }

    [HttpPost("restore-items")]
    public async Task<ActionResult<ApiResponse<RestoreBackupItemsResultDto>>> RestoreItemsAsync(
        [FromBody] RestoreBackupItemsRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await EnterActorAsync(cancellationToken);
        using var actorLease = actor.Lease;
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

        var hedef = (await backupService.GetBackupsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, request.BackupId, StringComparison.OrdinalIgnoreCase));
        var konu = await ResolveRecordSubjectAsync(hedef, cancellationToken);

        // Seçilen öğe listesi UZUN olabilir; kayıt sınırsız büyümesin diye ilk 10 tanesi adıyla,
        // kalanı sayıyla yazılır. "Kaç öğe" bilgisi hiçbir koşulda kaybolmaz.
        var ogeler = string.Join(", ", request.Items.Take(10))
            + (request.Items.Length > 10 ? $" ... (+{request.Items.Length - 10})" : string.Empty);

        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Restore, hedef?.Name ?? request.BackupId, konu, cancellationToken);

        RestoreBackupItemsResultDto result;
        try
        {
            result = await backupService.RestoreItemsAsync(request,
                (record, token) => RevalidateMutationAsync(actor.Authority, record, token), cancellationToken);
        }
        catch (Exception exception)
        {
            await WriteBackupAuditAsync(
                "backup.restore_failed",
                hedef?.Name ?? request.BackupId,
                $"Granular restore FAILED from {hedef?.Type}/{hedef?.Source}: {ogeler} — {exception.Message}",
                konu,
                CancellationToken.None);
            throw;
        }

        await WriteBackupAuditAsync(
            "backup.items_restored",
            hedef?.Name ?? request.BackupId,
            $"Granular restore from {hedef?.Type}/{hedef?.Source}: {result.RestoredCount} item(s) "
            + $"written to '{result.TargetRoot}'. Selected: {ogeler}",
            konu,
            cancellationToken);

        var response = CustomerAccess.IsScoped(User)
            ? new RestoreBackupItemsResultDto { RestoredCount = result.RestoredCount, TargetRoot = string.Empty }
            : result;
        return Ok(ApiResponse<RestoreBackupItemsResultDto>.Ok(response, "backup.items_restored"));
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
        var actor = await EnterActorAsync(cancellationToken);
        using var actorLease = actor.Lease;
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);

        var record = (await backupService.GetBackupsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, backupId, StringComparison.OrdinalIgnoreCase));

        // Konu, kayıt SİLİNMEDEN önce çözülür (sonrasında okunamaz).
        var konu = await ResolveRecordSubjectAsync(record, cancellationToken);

        await backupService.DeleteBackupAsync(backupId,
            (current, token) => RevalidateMutationAsync(actor.Authority, current, token), cancellationToken);

        await WriteBackupAuditAsync(
            "backup.deleted",
            record?.Name ?? backupId,
            $"Backup '{record?.Name ?? backupId}' ({record?.Type}/{record?.Source}) deleted.",
            konu,
            cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { id = backupId }, "backup.deleted"));
    }

    /// <summary>
    /// Customer/Reseller için arşiv sahibinin aboneliği erişilebilir olmalı; site yedeğinde
    /// ayrıca güncel site kapsamı da doğrulanır. Site ataması geçmiş arşivleri devretmez.
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

        await EnsureBackupOwnershipAsync(record, cancellationToken);
    }

    private async Task EnsureBackupOwnershipAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        if (!CustomerAccess.IsScoped(User)) return;

        var subscription = string.Equals(record.Type, "subscription", StringComparison.OrdinalIgnoreCase);
        var site = string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase);
        if (subscription || site)
        {
            if (!Guid.TryParseExact(record.SubscriptionUserId, "N", out var sahip) || sahip == Guid.Empty)
            {
                throw new AdaPanel.Core.Common.AppException("auth.subscription_forbidden", 403, 403);
            }

            await EnsureSubscriptionAccessAsync(sahip, cancellationToken);
            if (subscription) return;
        }

        if (!site)
        {
            throw new AdaPanel.Core.Common.AppException("auth.site_forbidden", 403, 403);
        }

        await CustomerAccess.EnsureSiteAsync(customerScopes, User, record.Source, cancellationToken);
    }

    private async Task<(BackupActorAuthority Authority, IDisposable Lease)> EnterActorAsync(
        CancellationToken token, bool administratorOnly = false)
    {
        var authority = await BackupActorAuthority.CaptureAsync(dbContext, customerScopes, User, token);
        if (administratorOnly && authority.Role != "Administrator")
            throw new AdaPanel.Core.Common.AppException("auth.forbidden", 403, 403);
        // User/subscription mutations use the same outer lease. Never acquire it from
        // a source callback: all paths take subscription before the backup source.
        var lease = await DatabaseLifecycle.EnterSubscriptionAsync(authority.SubscriptionId, token);
        try
        {
            await authority.RevalidateAsync(dbContext, customerScopes, User, token);
            return (authority, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    // Reads hold no subscription/source mutation lease while archive IO runs. The
    // accepted actor and cookie must still be valid at the response admission boundary.
    // A PanelToken principal has no cookie sid/stamp and keeps its existing contract.
    private sealed record ReadAuthority(BackupActorAuthority Actor, string? SessionId, bool Cookie);

    private async Task<ReadAuthority> CaptureReadAuthorityAsync(CancellationToken token)
    {
        var cookie = User.Identities.Any(identity => identity.IsAuthenticated &&
            identity.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme);
        var sessionId = User.FindFirstValue(SessionRevocationStore.SessionIdClaim);
        if (cookie && (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(User.FindFirstValue(CookieSessionValidator.CredentialStampClaim))))
            throw new AdaPanel.Core.Common.AppException("auth.unauthorized", 401, 401);
        var actor = await BackupActorAuthority.CaptureAsync(dbContext, customerScopes, User, token);
        var authority = new ReadAuthority(actor, sessionId, cookie);
        await EnsureReadSessionAsync(authority, token);
        return authority;
    }

    private async Task RevalidateReadAuthorityAsync(ReadAuthority authority, CancellationToken token)
    {
        await authority.Actor.RevalidateAsync(dbContext, customerScopes, User, token);
        await EnsureReadSessionAsync(authority, token);
    }

    private async Task EnsureReadSessionAsync(ReadAuthority authority, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (authority.Cookie && (User.FindFirstValue(SessionRevocationStore.SessionIdClaim) != authority.SessionId ||
            await HttpContext.RequestServices.GetRequiredService<SessionRevocationStore>()
                .IsRevokedAsync(authority.SessionId, token)))
            throw new AdaPanel.Core.Common.AppException("auth.unauthorized", 401, 401);
        token.ThrowIfCancellationRequested();
    }

    private async Task RevalidateMutationAsync(BackupActorAuthority authority, BackupRecordDto record, CancellationToken token)
    {
        await authority.RevalidateAsync(dbContext, customerScopes, User, token);
        await EnsureBackupOwnershipAsync(record, token);
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
        var actor = await EnterActorAsync(cancellationToken);
        using var actorLease = actor.Lease;
        async Task BeforeBackup(CancellationToken token)
        {
            await actor.Authority.RevalidateAsync(dbContext, customerScopes, User, token);
            await EnsureSubscriptionAccessAsync(request.UserId, token);
        }
        // KİRACI İZOLASYONU: kapsamlı kullanıcı gövdeye BAŞKA bir abonelik kimliği yazamaz.
        // Kimlik istekten okunup doğrulanmasaydı, bir müşteri başka bir müşterinin bütün
        // verisini içeren bir arşiv ürettirip indirebilirdi.
        await EnsureSubscriptionAccessAsync(request.UserId, cancellationToken);

        // Abonelik yedeği müşterinin BÜTÜN sitelerini, veritabanlarını ve postasını tek arşive
        // koyar — paneldeki en uzun süren iş budur. Etiket abonelik adı değil kimliğidir: adı
        // çözmek ikinci bir sorgu ister ve bu uç zaten kimlikle çalışır.
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Backup, request.UserId.ToString("N"), request.UserId, cancellationToken);

        var backup = await backupService.BackupSubscriptionAsync(request, BeforeBackup, cancellationToken);

        // Konu ÖLÇÜLMEZ, İSTEKTEN GELİR ve zaten yukarıda doğrulanmıştır: bu arşiv o aboneliğin
        // bütün verisini taşır, izi de sahibinin ekranında görünmelidir.
        await WriteBackupCreatedAuditAsync(backup, request.UserId, cancellationToken);

        return Ok(ApiResponse<BackupRecordDto>.Ok(ProjectBackupRecord(backup), "backup.created"));
    }

    /// <summary>
    /// Yedeğin artımsal ZİNCİRİ: tam yedekten hedefe sıralı halkalar + kopuk mu bilgisi.
    /// Ekran, geri yüklemeyi denemeden önce eksik halkaları gösterebilsin diye.
    /// </summary>
    [HttpGet("{backupId}/chain")]
    public async Task<ActionResult<ApiResponse<BackupChainDto>>> GetChainAsync(string backupId, CancellationToken cancellationToken)
    {
        var authority = await CaptureReadAuthorityAsync(cancellationToken);
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);
        var chain = await backupService.GetChainAsync(backupId, cancellationToken);
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);
        await RevalidateReadAuthorityAsync(authority, cancellationToken);
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
    public async Task<ActionResult<ApiResponse<BackupDeleteResultDto>>> DeleteChainAsync(string backupId, CancellationToken cancellationToken,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] DeleteBackupChainRequest? request = null)
    {
        var actor = await EnterActorAsync(cancellationToken);
        using var actorLease = actor.Lease;
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);

        var record = (await backupService.GetBackupsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, backupId, StringComparison.OrdinalIgnoreCase));
        var konu = await ResolveRecordSubjectAsync(record, cancellationToken);

        var result = await backupService.DeleteBackupChainAsync(backupId, request?.ExpectedBackupIds,
            (current, token) => RevalidateMutationAsync(actor.Authority, current, token), cancellationToken);

        await WriteBackupAuditAsync(
            "backup.chain_deleted",
            record?.Name ?? backupId,
            $"Backup chain deleted ({result.DeletedIds.Length} link(s)): {string.Join(", ", result.DeletedNames)}",
            konu,
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

        // KONU = ABONELİĞİN SAHİBİ. Eskiden konusuz yazılıyordu; yönetici bir müşterinin yedek
        // programını kapattığında müşteri bunu kendi denetim ekranında göremiyor, yedeklerinin ne
        // zaman ve kimin eliyle durduğunu ancak yedeklerin yokluğundan anlıyordu.
        await WriteBackupAuditAsync(
            "backup.schedule_changed",
            $"backup:schedule:{userId:N}",
            $"Subscription backup schedule updated for {userId:N}: enabled={schedule.Enabled}, " +
            $"every {schedule.IntervalHours}h, keep {schedule.RetentionDays} day(s)/{schedule.KeepCount} copy, " +
            $"incremental={schedule.Incremental}, throttled={schedule.ThrottledByQuota}.",
            userId,
            cancellationToken);

        return Ok(ApiResponse<SubscriptionBackupScheduleDto>.Ok(schedule, "backup.schedule_saved"));
    }

    [HttpDelete("schedules/{userId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteScheduleAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureSubscriptionAccessAsync(userId, cancellationToken);
        await backupSchedules.RemoveScheduleAsync(userId, cancellationToken);

        // İZ YOKTU. Programı silmek, aboneliğin OTOMATİK YEDEĞİNİ SESSİZCE DURDURUR: hiçbir hata
        // görünmez, yalnızca bir daha yedek alınmaz. Felaket anında "yedeğim neden yok" sorusunun
        // tek cevabı bu satırdır. Konu abonelik sahibidir; müşteri, programını kimin kaldırdığını
        // kendi ekranından görebilmeli.
        await WriteBackupAuditAsync(
            "backup.schedule_deleted",
            $"backup:schedule:{userId:N}",
            $"Subscription backup schedule removed for {userId:N}; automatic backups stopped.",
            userId,
            cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { userId }, "backup.schedule_deleted"));
    }

    /// <summary>
    /// Programı beklemeden şimdi çalıştırır. Günlük kota bu yolu ENGELLEMEZ: müşteri riskli bir
    /// işlemden önce yedek almak istediğinde onu durdurmak, korumanın kendisini durdurmak olurdu.
    /// </summary>
    [HttpPost("schedules/{userId:guid}/run")]
    public async Task<ActionResult<ApiResponse<SubscriptionBackupScheduleDto>>> RunScheduleNowAsync(Guid userId, CancellationToken cancellationToken)
    {
        var actor = await EnterActorAsync(cancellationToken);
        using var actorLease = actor.Lease;
        async Task BeforeMutation(CancellationToken token)
        {
            await actor.Authority.RevalidateAsync(dbContext, customerScopes, User, token);
            await EnsureSubscriptionAccessAsync(userId, token);
        }
        await EnsureSubscriptionAccessAsync(userId, cancellationToken);

        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Backup, userId.ToString("N"), userId, cancellationToken);

        var schedule = await backupSchedules.RunNowAsync(userId, BeforeMutation, cancellationToken);

        // İZ YOKTU. Elle tetiklenen tur yalnız yedek ALMAZ, saklama politikasına göre ESKİLERİ DE
        // BUDAR — yani veri silen bir yoldur. Ayrıca arka plan turundan ayırt edilebilmesi gerekir:
        // bu kayıt gerçek aktörü ve IP'yi taşır, zamanlayıcının turu taşımaz.
        await WriteBackupAuditAsync(
            "backup.schedule_run",
            $"backup:schedule:{userId:N}",
            $"Subscription backup run triggered manually for {userId:N} "
            + $"(retention {schedule.RetentionDays} day(s)/{schedule.KeepCount} copy, incremental={schedule.Incremental}).",
            userId,
            cancellationToken);

        return Ok(ApiResponse<SubscriptionBackupScheduleDto>.Ok(schedule, "backup.schedule_run"));
    }
}
