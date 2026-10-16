using System.Runtime.Versioning;
using System.Text;
using AdaPanel.Core.Dtos.Automation;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Automation;

/// <summary>
/// Panel içi zamanlanmış otomasyonların (otomatik yedekleme + SSL yenileme) iş mantığı.
/// Ayarlar ve son çalışma durumu <c>config/automation.json</c> dosyasında tutulur.
/// Arka plan worker'ı zamanı gelen job'ları <see cref="RunDueJobsAsync"/> üzerinden tetikler.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutomationService(
    JsonFileStore fileStore,
    IBackupService backupService,
    ISslService sslService,
    IMailService mailService,
    IIisSiteService iisSiteService,
    IDnsService dnsService,
    IComponentBootstrapper componentBootstrapper,
    ISelfUpdateService selfUpdateService,
    IDatabaseService databaseService,
    IBruteForceProtectionService bruteForceProtection,
    ISecretProtector secretProtector,
    INotificationService notificationService,
    ISystemMonitoringService systemMonitoringService,
    IHealthService healthService,
    ISchedulerService schedulerService,
    IErrorReportSender errorReportSender,
    IConcurrencyLock concurrencyLock,
    // Günlük silmenin TEK kaynağı. Otomasyonun kendi (korumasız, özyinelemeli) kopyası kaldırıldı;
    // gerekçe RunLogCleanupAsync'te.
    Logs.ILogRotationService logRotationService,
    // Hangi veritabanının hangi siteye ait olduğunun OTORİTE kaynağı. Otomatik yedek, site
    // arşivine hangi dökümleri koyacağını buradan öğrenir — ad benzerliğinden DEĞİL.
    IDatabaseOwnershipService databaseOwnership,
    IRunningJobService runningJobs,
    ILogger<AutomationService> logger,
    ISiteDatabaseLinkService? siteDatabaseLinks = null,
    ICustomerScopeService? customerScopes = null) : IAutomationService
{
    private const string StatePath = "config/automation.json";
    private const string AlertCooldownPath = "config/automation-alerts.json";
    private const string SslStatePath = "config/ssl.json";
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromHours(24);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMinutes(5);
    // NOT: burada `DefaultEngines = ["mysql"]` diye sabit bir liste vardı ve KALDIRILDI.
    // Motorlar artık kurulu olanlardan türetiliyor (bkz. ResolveInstalledEnginesAsync).

    public async Task<AutomationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return ToStatus(state);
    }

    public async Task<AutomationStatusDto> UpdateSettingsAsync(UpdateAutomationSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var state = await fileStore.UpdateAsync(StatePath, new AutomationStateRecord(), state =>
        {
            state.AutoBackupEnabled = request.AutoBackupEnabled;

            // Program NORMALİZE edilerek saklanır: mod/saat/gün doğrulaması tek yerde (BackupSchedule)
            // yapılır, ekranın gönderdiği değere güvenilmez.
            var plan = BackupSchedule.Normalize(
                request.BackupScheduleMode,
                request.BackupIntervalHours,
                request.BackupScheduleHour,
                request.BackupScheduleMinute,
                request.BackupScheduleDayOfWeek,
                request.BackupScheduleDayOfMonth,
                request.BackupScheduleSecondHour,
                request.BackupScheduleSecondMinute);
            state.BackupScheduleMode = plan.Mode;
            state.BackupIntervalHours = plan.IntervalHours;
            state.BackupScheduleHour = plan.Hour;
            state.BackupScheduleMinute = plan.Minute;
            state.BackupScheduleDayOfWeek = plan.DayOfWeek;
            state.BackupScheduleDayOfMonth = plan.DayOfMonth;
            state.BackupScheduleSecondHour = plan.SecondHour;
            state.BackupScheduleSecondMinute = plan.SecondMinute;
            state.BackupRetentionDays = Math.Clamp(request.BackupRetentionDays, 0, 3650);
            state.BackupSites = request.BackupSites;
            state.BackupDatabases = request.BackupDatabases;
            state.BackupMail = request.BackupMail;
            state.BackupPanelConfig = request.BackupPanelConfig;
            state.BackupDatabaseEngines = NormalizeEngines(request.BackupDatabaseEngines);
            state.AutoSslRenewEnabled = request.AutoSslRenewEnabled;
            state.SslRenewIntervalHours = Math.Clamp(request.SslRenewIntervalHours, 1, 24 * 30);

            // Uzak yedek hedefi: parola DPAPI ile korunur; boş gönderilirse mevcut korunur.
            // Hedef listesi BackupService'in tek kaynağından gelir; burada elle liste tutulmaz.
            var targetType = Backup.BackupService.NormalizeScheduleTargetType(request.BackupTargetType);
            state.BackupTargetType = targetType;
            state.BackupRemoteUrl = request.BackupRemoteUrl?.Trim() ?? string.Empty;
            state.BackupRemoteUserName = request.BackupRemoteUserName?.Trim() ?? string.Empty;
            state.BackupRemoteRegion = request.BackupRemoteRegion?.Trim() ?? string.Empty;
            if (state.BackupTargetType == "local")
            {
                state.BackupRemotePasswordProtected = string.Empty;
            }
            else if (!string.IsNullOrEmpty(request.BackupRemotePassword))
            {
                state.BackupRemotePasswordProtected = secretProtector.Protect(request.BackupRemotePassword);
            }

            // IIS site log rotasyonu (Plesk log rotation): eski erişim logları dolan diski sessizce büyütür.
            state.LogCleanupEnabled = request.LogCleanupEnabled;
            state.LogRetentionDays = Math.Clamp(request.LogRetentionDays, 1, 3650);

            state.UpdateCheckEnabled = request.UpdateCheckEnabled;
            state.AutoApplyComponentUpdates = request.AutoApplyComponentUpdates;

            state.AlertsEnabled = request.AlertsEnabled;
            state.DiskUsageThresholdPercent = Math.Clamp(request.DiskUsageThresholdPercent, 50, 99);
            state.CertExpiryWarningDays = Math.Clamp(request.CertExpiryWarningDays, 1, 90);
            state.MemoryUsageThresholdPercent = Math.Clamp(request.MemoryUsageThresholdPercent, 50, 99);
            state.ServiceDownAlertsEnabled = request.ServiceDownAlertsEnabled;
            return state;
        }, cancellationToken);
        return ToStatus(state);
    }

    public Task<AutomationStatusDto> RunBackupNowAsync(CancellationToken cancellationToken = default) =>
        RunBackupNowCoreAsync(null, cancellationToken);

    public Task<AutomationStatusDto> RunBackupNowAsync(Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default) => RunBackupNowCoreAsync(beforeMutation, cancellationToken);

    private async Task<AutomationStatusDto> RunBackupNowCoreAsync(
        Func<CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var expectedContext = beforeMutation is null ? null : BackupContext(await LoadAsync(cancellationToken));
        var approval = beforeMutation is null ? null : new BackupExecutionApproval(async token =>
        {
            await beforeMutation(token);
            if (BackupContext(await LoadAsync(token)) != expectedContext)
                throw new AppException("backup.schedule_changed", 409, 409);
        });
        // Kilidi bekleyen ikinci bir istek, ilk tur bitince tekrar yedek başlatmamalı.
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Backup, "automation", null, cancellationToken);
        await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Automation, async () =>
        {
            var state = await LoadAsync(cancellationToken);
            if (approval is not null) await approval.CheckAsync(cancellationToken);
            await RunBackupAsync(state, cancellationToken, approval);
            if (approval is not null) await approval.CheckAsync(cancellationToken);
            await SaveRunResultsAsync(state, cancellationToken, expectedContext);
        }, cancellationToken);

        return await GetStatusAsync(cancellationToken);
    }

    public Task<AutomationStatusDto> RunSslRenewNowAsync(CancellationToken cancellationToken = default) =>
        RunSslRenewNowCoreAsync(null, cancellationToken);

    public Task<AutomationStatusDto> RunSslRenewNowAsync(Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default) => RunSslRenewNowCoreAsync(beforeMutation, cancellationToken);

    private async Task<AutomationStatusDto> RunSslRenewNowCoreAsync(
        Func<CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var approval = beforeMutation is null ? null : new BackupExecutionApproval(beforeMutation);
        await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Automation, async () =>
        {
            var state = await LoadAsync(cancellationToken);
            if (approval is not null) await approval.CheckAsync(cancellationToken);
            await RunSslRenewAsync(state, cancellationToken, approval);
            if (approval is not null) await approval.CheckAsync(cancellationToken);
            await SaveRunResultsAsync(state, cancellationToken);
        }, cancellationToken);

        return await GetStatusAsync(cancellationToken);
    }

    public async Task<BackupRemoteTestResultDto> TestRemoteBackupAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default)
    {
        var password = request.RemotePassword;
        if (string.IsNullOrEmpty(password))
        {
            // İstekte parola yoksa kayıtlı (korumalı) parolayı kullan → kullanıcı kayıtlı hedefi tekrar parola girmeden test eder.
            var state = await LoadAsync(cancellationToken);
            password = ResolveRemotePassword(state);
        }

        return await backupService.TestRemoteTargetAsync(
            new BackupRemoteTestRequest
            {
                TargetType = request.TargetType,
                RemoteUrl = request.RemoteUrl,
                RemoteUserName = request.RemoteUserName,
                RemotePassword = password,
                RemoteRegion = request.RemoteRegion
            },
            cancellationToken);
    }

    public async Task RunDueJobsAsync(CancellationToken cancellationToken = default)
    {
        // Süresi dolmuş brute-force engellerini temizle. Paylaşılan kilit reentrant
        // olmadığından bu çağrı exclusive bloğun dışında kalmalı.
        await bruteForceProtection.CleanupExpiredBansAsync(cancellationToken);

        // Bekleyen hata bildirimlerini gönder ve eskileri temizle. Exclusive bloğun DIŞINDA:
        // ağ çağrısı içeriyor ve paylaşılan kilit reentrant değil — kilidi ağ beklerken tutmak
        // panelin tamamını (giriş dahil) bloklardı. Toplayıcı adresi boşsa hiçbir şey yapılmaz.
        try
        {
            await errorReportSender.SendPendingAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Bildirim gönderimi ASLA panelin işleyişini etkilememeli: bu yalnız telemetri.
            logger.LogDebug(exception, "Hata bildirimleri gönderilemedi.");
        }

        // Kalıcı izleme geçmişi: her turda (dakikada bir) CPU/RAM/ağ örneği biriktir → izleme ekranı
        // trend grafiklerini bununla doldurur (sayfa yenilense de dolu). Best-effort; ana akışı bozmaz.
        try
        {
            await systemMonitoringService.RecordSampleAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "İzleme geçmişi örneği alınamadı.");
        }

        // Uyarı bildirimleri exclusive bloğun DIŞINDA (SMTP yavaş olabilir, kendi
        // cooldown dosyasını kullanır, otomasyon kilidini meşgul etmez).
        try
        {
            await RunAlertChecksAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Uyarı bildirimi kontrolü başarısız oldu.");
        }

        // DNS bind nöbetçisi (kilit DIŞINDA; kendi soğuma süresini dosyada tutar). Beş dakikada bir:
        // DNS servisi çalışıyor ama genel IP'ye bind olmamışsa kendiliğinden yeniden başlatır.
        // GERÇEK OLAY: 2026-07-23 reboot'unda servis loopback'e bind kaldı ve ALTI GÜN boyunca
        // fark edilmedi (tüm alan adları dışarıdan çözülemedi, Let's Encrypt DNS-01 patladı).
        // Ayar YOK, her zaman açık: bozuk DNS = tam kesinti, kimse bunu kapalı tutmak istemez.
        // Aralık kısıtlamasını DnsService kendi durum dosyasında uygular; aralık dolmadıysa null döner.
        try
        {
            var dnsHealth = await dnsService.RunBindWatchdogAsync(cancellationToken);
            if (dnsHealth is not null)
            {
                if (dnsHealth.NeedsRepair)
                {
                    logger.LogError(
                        "DNS sunucusu genel adreste dinlemiyor ve otomatik onarılamadı: {Details}",
                        dnsHealth.Details);
                }
                else if (dnsHealth.LastAutoRepairUtc is { } repairedAt &&
                         DateTime.UtcNow - repairedAt < WatchdogInterval)
                {
                    logger.LogWarning(
                        "DNS bind nöbetçisi devreye girdi: {Status}",
                        dnsHealth.LastAutoRepairStatus);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DNS bind nöbetçisi çalıştırılamadı.");
        }

        // Bileşen güncelleme kontrolü günde bir. KİLİDİN DIŞINDA çalışmalı: hem uzun süren ağ
        // istekleri hem de otomatik kurulum yüzünden. Kurulum (ör. Roundcube/phpMyAdmin) IIS sitesi
        // oluşturur, o da AYNI non-reentrant kilidi almaya çalışır → kilit içinde çağrılırsa panel
        // KALICI OLARAK KİLİTLENİR. Sonuç aşağıda, kilit içinde state'e yazılır.
        UpdateCheckOutcome? updateOutcome = null;
        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            if (snapshot.UpdateCheckEnabled && IsDue(snapshot.LastUpdateCheckRunUtc, 24, DateTime.UtcNow))
            {
                updateOutcome = await RunUpdateCheckAsync(snapshot.AutoApplyComponentUpdates, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Bileşen güncelleme kontrolü çalıştırılamadı.");
        }

        await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Automation, async () =>
        {
            var state = await LoadAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var changed = false;

            // YEDEĞİN ZAMANI: karar tek kaynaktan (BackupSchedule) ve SAF olarak gelir. Saat
            // dilimi her turda yeniden okunur — yönetici sunucunun dilimini değiştirdiğinde
            // "03:00" yeni dilimde de 03:00 olsun diye (bkz. BackupSchedule notları).
            if (state.AutoBackupEnabled &&
                BackupSchedule.ZamaniGeldi(BuildSchedulePlan(state), state.LastBackupRunUtc, now, ServerTimeZone()))
            {
                IAsyncDisposable? job = null;
                try
                {
                    job = await runningJobs.BeginAsync(RunningJobKinds.Backup, "automation", null, cancellationToken);
                }
                catch (AppException exception) when (exception.HttpStatusCode == 409)
                {
                    // Başka sunucu yedeği sürüyor: zamanı tüketmeden sonraki turda tekrar dene.
                    logger.LogDebug("Otomatik yedekleme, süren yedek işinin bitmesini bekliyor.");
                }
                if (job is not null)
                {
                    await using (job)
                    {
                        await RunBackupAsync(state, cancellationToken);
                        changed = true;
                    }
                }
            }

            // The preceding backup can take hours while settings remain editable. Start the
            // next job from current SSL settings/results, and preserve those results if skipped.
            var currentSsl = await LoadAsync(cancellationToken);
            state.AutoSslRenewEnabled = currentSsl.AutoSslRenewEnabled;
            state.SslRenewIntervalHours = currentSsl.SslRenewIntervalHours;
            state.LastSslRunUtc = currentSsl.LastSslRunUtc;
            state.LastSslStatus = currentSsl.LastSslStatus;
            state.LastSslDetails = currentSsl.LastSslDetails;
            if (state.AutoSslRenewEnabled && IsDue(state.LastSslRunUtc, state.SslRenewIntervalHours, DateTime.UtcNow))
            {
                await RunSslRenewAsync(state, cancellationToken);
                changed = true;
            }

            // Log temizliği günde bir çalışır (aralık ayarı yok; retention gün sayısı ayarlıdır).
            if (state.LogCleanupEnabled && IsDue(state.LastLogCleanupRunUtc, 24, now))
            {
                await RunLogCleanupAsync(state, cancellationToken);
                changed = true;
            }

            // Güncelleme kontrolünün SONUCU burada işlenir; işin kendisi kilidin DIŞINDA koştu.
            if (updateOutcome is not null)
            {
                ApplyUpdateOutcome(state, updateOutcome);
                changed = true;
            }

            if (changed)
            {
                await SaveRunResultsAsync(state, cancellationToken);
            }
        }, cancellationToken);
    }

    private async Task RunBackupAsync(AutomationStateRecord state, CancellationToken cancellationToken,
        BackupExecutionApproval? approval = null)
    {
        var summary = new StringBuilder();
        var hasError = false;
        var siteCount = 0;
        var dbCount = 0;

        // Sitenin arşivine giren veritabanları: aynı dökümü bir de tek başına yedeklemeyelim diye
        // hangilerinin siteye gittiği burada tutulur (anahtar "motor|ad").
        var siteIcindeYedeklenen = new HashSet<string>(StringComparer.Ordinal);
        var bagimsizDenenen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            if (state.BackupSites)
            {
                var sites = await iisSiteService.GetSitesAsync(cancellationToken);
                var remotePassword = ResolveRemotePassword(state);

                // Sahiplik kaydı BİR KEZ okunur ve siteye göre gruplanır: site başına ayrı sorgu
                // atmak, onlarca siteli bir sunucuda gecelik işi gereksiz yere uzatırdı.
                var sahiplik = await databaseOwnership.GetAllAsync(cancellationToken);
                var siteVeritabanlari = await ResolveDatabasesBySiteAsync(sahiplik,
                    sites.Select(site => site.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), cancellationToken);

                foreach (var site in sites)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // TEK ARŞİV, İÇİNDE AYRI AYRI: files/ + config/ + databases/ + mail/.
                        // Eskiden otomatik yedek siteyi dosyalarıyla, veritabanlarını ayrı ayrı ve
                        // postayı bambaşka bir arşivle alıyordu; bir siteyi geri getirmek için
                        // kullanıcının parçaları elle eşleştirmesi gerekiyordu. Artık bir sitenin
                        // her şeyi tek dosyada ve içinde ayrılmış hâlde duruyor.
                        siteVeritabanlari.TryGetValue(site.Name, out var dbListesi);
                        var dbler = (dbListesi ?? [])
                            .Select(x => new BackupDatabaseRef { Engine = x.Engine, DatabaseName = x.DatabaseName })
                            .ToList();

                        var request = new BackupSiteRequest
                        {
                            SiteName = site.Name,
                            IncludeFiles = true,
                            IncludeConfig = true,
                            IncludeMail = true,
                            Databases = dbler,
                            TargetType = state.BackupTargetType,
                            RemoteUrl = state.BackupRemoteUrl,
                            RemoteUserName = state.BackupRemoteUserName,
                            RemotePassword = remotePassword,
                            RemoteRegion = state.BackupRemoteRegion
                        };
                        var record = approval is null
                            ? await backupService.BackupSiteAsync(request, cancellationToken)
                            : await backupService.BackupSiteAsync(request, approval.CheckAsync, cancellationToken);

                        var outcome = EvaluateBackupResult(record, "site", site.Name, dbler);
                        siteIcindeYedeklenen.UnionWith(outcome.IncludedDatabases);
                        if (outcome.Complete) siteCount++;
                        else
                        {
                            hasError = true;
                            summary.AppendLine($"site {site.Name}: {outcome.Detail}");
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException && approval?.Rejected(exception) != true)
                    {
                        hasError = true;
                        summary.AppendLine($"site {site.Name}: {exception.Message}");
                        logger.LogWarning(exception, "Otomatik yedek başarısız: site {SiteName}", site.Name);
                    }
                }
            }

            if (state.BackupDatabases)
            {
                // MOTOR LİSTESİ KURULU OLANDAN TÜRETİLİR, SABİT DEĞİLDİR.
                //
                // Burada eskiden `DefaultEngines = ["mysql"]` sabiti vardı. AdaPanel'in kendi
                // kurduğu motor MariaDB'dir; yani VARSAYILAN kurulumda gecelik yedek her gece
                // "mysql" motorunu soruyor, `database.engine_not_installed` alıyor ve
                // **sıfır veritabanı yedekliyordu**. Özet satırı "0 veritabanı yedeklendi" diyordu
                // ama bu bir bilgi değil, sessiz bir arızaydı: kullanıcı veritabanlarının
                // yedeklendiğini sanıyordu.
                //
                // Kullanıcı motorları AÇIKÇA seçtiyse ona dokunulmaz (kurulu olmayan bir motoru
                // seçmişse bunu bilmesi gerekir, hata mesajı bilinçli olarak korunur).
                var engines = state.BackupDatabaseEngines.Count > 0
                    ? state.BackupDatabaseEngines
                    : await ResolveInstalledEnginesAsync(cancellationToken);
                foreach (var engine in engines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var workspace = await databaseService.GetWorkspaceAsync(engine, cancellationToken);
                        foreach (var database in workspace.Databases)
                        {
                            // Bu veritabanı bir sitenin arşivine ZATEN girdiyse tekrar yedeklenmez:
                            // aynı dökümü iki dosyaya yazmak yeri iki katına çıkarır ve geri
                            // yüklerken "hangisi güncel" sorusunu doğurur. Sahipsiz (hiçbir siteye
                            // bağlı olmayan) veritabanları BURADA yedeklenmeye devam eder — yoksa
                            // panel dışında açılmış bir veritabanı sessizce yedeksiz kalırdı.
                            var identity = DatabaseResourceIdentity.Key(engine, database.Name);
                            if (siteIcindeYedeklenen.Contains(identity) || !bagimsizDenenen.Add(identity))
                            {
                                continue;
                            }

                            try
                            {
                                var request = new BackupDatabaseRequest
                                {
                                    Engine = engine,
                                    DatabaseName = database.Name,
                                    TargetType = state.BackupTargetType,
                                    RemoteUrl = state.BackupRemoteUrl,
                                    RemoteUserName = state.BackupRemoteUserName,
                                    RemotePassword = ResolveRemotePassword(state),
                                    RemoteRegion = state.BackupRemoteRegion
                                };
                                var record = approval is null
                                    ? await backupService.BackupDatabaseAsync(request, cancellationToken)
                                    : await backupService.BackupDatabaseAsync(request, approval.CheckAsync, cancellationToken);
                                var outcome = EvaluateBackupResult(record, "database", $"{engine}:{database.Name}",
                                    [new BackupDatabaseRef { Engine = engine, DatabaseName = database.Name }]);
                                if (outcome.Complete) dbCount++;
                                else
                                {
                                    hasError = true;
                                    summary.AppendLine($"db {engine}/{database.Name}: {outcome.Detail}");
                                }
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException && approval?.Rejected(exception) != true)
                            {
                                hasError = true;
                                summary.AppendLine($"db {engine}/{database.Name}: {exception.Message}");
                                logger.LogWarning(exception, "Otomatik veritabanı yedeği başarısız: {Engine}/{Database}", engine, database.Name);
                            }
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException && approval?.Rejected(exception) != true)
                    {
                        hasError = true;
                        summary.AppendLine($"engine {engine}: {exception.Message}");
                        logger.LogWarning(exception, "Otomatik veritabanı yedeği motoru başarısız: {Engine}", engine);
                    }
                }
            }

            if (state.BackupMail)
            {
                try
                {
                    var request = new BackupMailRequest
                    {
                        TargetType = state.BackupTargetType,
                        RemoteUrl = state.BackupRemoteUrl,
                        RemoteUserName = state.BackupRemoteUserName,
                        RemotePassword = ResolveRemotePassword(state),
                        RemoteRegion = state.BackupRemoteRegion
                    };
                    var record = approval is null
                        ? await backupService.BackupMailAsync(request, cancellationToken)
                        : await backupService.BackupMailAsync(request, approval.CheckAsync, cancellationToken);
                    var outcome = EvaluateBackupResult(record, "mail", null, []);
                    hasError |= !outcome.Complete;
                    summary.AppendLine(outcome.Complete ? "mail: yedeklendi" : $"mail: {outcome.Detail}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException && approval?.Rejected(exception) != true)
                {
                    hasError = true;
                    summary.AppendLine($"mail: {exception.Message}");
                    logger.LogWarning(exception, "Otomatik mail yedeği başarısız.");
                }
            }

            if (state.BackupPanelConfig)
            {
                try
                {
                    var request = new BackupPanelRequest
                    {
                        TargetType = state.BackupTargetType,
                        RemoteUrl = state.BackupRemoteUrl,
                        RemoteUserName = state.BackupRemoteUserName,
                        RemotePassword = ResolveRemotePassword(state),
                        RemoteRegion = state.BackupRemoteRegion
                    };
                    var record = approval is null
                        ? await backupService.BackupPanelAsync(request, cancellationToken)
                        : await backupService.BackupPanelAsync(request, approval.CheckAsync, cancellationToken);
                    var outcome = EvaluateBackupResult(record, "panel", "panel", []);
                    hasError |= !outcome.Complete;
                    summary.AppendLine(outcome.Complete ? "panel: yedeklendi" : $"panel: {outcome.Detail}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException && approval?.Rejected(exception) != true)
                {
                    hasError = true;
                    summary.AppendLine($"panel: {exception.Message}");
                    logger.LogWarning(exception, "Otomatik panel yedeği başarısız.");
                }
            }

            // Uzak rotasyon: hedef off-site ise budama uzak kopyaları da silsin (kimlikleri güncel ayardan geçir).
            var pruneRemote = string.Equals(state.BackupTargetType, "local", StringComparison.OrdinalIgnoreCase)
                ? null
                : new BackupPruneRemoteOptions
                {
                    RemoteUrl = state.BackupRemoteUrl,
                    UserName = state.BackupRemoteUserName,
                    Password = ResolveRemotePassword(state),
                    Region = state.BackupRemoteRegion
                };
            // DIKKAT: buraya hasError=true iken de gelinir (her kaynak ayri try/catch ile yedeklenir).
            // Budamanin kendisi bu yuzden KAYNAK BAZINDA korumali: cutoff sonrasi kullanilabilir
            // yedegi olmayan bir kaynagin eskilerine dokunmaz (bkz. BackupService.PruneAsync).
            // Burada "hata varsa hic budama" demek, tek bir sitenin hatasi yuzunden butun sunucunun
            // saklama politikasini durdurmak olurdu.
            var pruned = approval is null
                ? await backupService.PruneAsync(state.BackupRetentionDays, pruneRemote, cancellationToken)
                : await backupService.PruneAsync(state.BackupRetentionDays, pruneRemote, approval.CheckAsync, cancellationToken);

            var budama = SummarizePrune(pruned);
            foreach (var satir in budama.TechnicalLines)
            {
                summary.AppendLine(satir);
            }

            state.LastBackupRunUtc = DateTime.UtcNow;
            state.LastBackupStatus = ResolveBackupStatus(hasError, budama);
            state.LastBackupDetails = BuildDetails(
                $"{siteCount} site, {dbCount} veritabanı yedeklendi, {budama.Sentence}.",
                summary.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (approval?.Rejected(exception) != true)
        {
            state.LastBackupRunUtc = DateTime.UtcNow;
            state.LastBackupStatus = "failed";
            state.LastBackupDetails = exception.Message;
            logger.LogError(exception, "Otomatik yedekleme job'u başarısız oldu.");
        }
    }

    private sealed record BackupOutcome(bool Complete, HashSet<string> IncludedDatabases, string Detail);

    private static BackupOutcome EvaluateBackupResult(
        BackupRecordDto? record,
        string expectedType,
        string? expectedSource,
        IReadOnlyCollection<BackupDatabaseRef> requestedDatabases)
    {
        var included = new HashSet<string>(StringComparer.Ordinal);
        if (record is null)
            return new(false, included, "backup_result=unconfirmed; reason=no_result");

        var knownStatus = record.Status is "created" or "created_remote_synced" or "created_with_errors" or "created_remote_failed";
        var sourceMatches = expectedSource is null
            ? !string.IsNullOrWhiteSpace(record.Source)
            : expectedType == "database"
                ? DatabaseSourceMatches(record.Source, expectedSource)
                : string.Equals(record.Source?.Trim(), expectedSource.Trim(), StringComparison.OrdinalIgnoreCase);
        var correctContext = string.Equals(record.Type, expectedType, StringComparison.OrdinalIgnoreCase) && sourceMatches;
        var localCopy = !string.IsNullOrWhiteSpace(record.Path) && !record.LocalMissing;
        var remoteCopy = !string.IsNullOrWhiteSpace(record.RemoteUri) && !record.RemoteMissing &&
            record.RemoteState is not (BackupPresence.Missing or BackupPresence.None) &&
            (record.RemoteState == BackupPresence.Present || record.Status is "created_remote_synced" or "created_with_errors");
        var usable = correctContext && knownStatus && !string.IsNullOrWhiteSpace(record.Id) &&
            record.Size > 0 && record.IntegrityState == BackupIntegrity.Sound &&
            !record.AllCopiesMissing && (localCopy || remoteCopy);
        var warnings = record.Warnings ?? [];

        // Inclusion is an explicit positive measurement. Status or a warning-free result
        // alone cannot prove that a requested database made it into the archive.
        if (usable && record.IncludedDatabases is not null)
        {
            var measured = record.IncludedDatabases
                .Where(db => db is not null && !string.IsNullOrWhiteSpace(db.Engine) && !string.IsNullOrWhiteSpace(db.DatabaseName))
                .Select(db => DatabaseResourceIdentity.Key(db.Engine, db.DatabaseName))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var warning in warnings.Where(w => w.MessageKey is "backup.database_dump_empty" or "backup.database_dump_failed"))
            {
                var target = warning.Target ?? string.Empty;
                var separator = target.IndexOf('/');
                if (separator > 0)
                    measured.Remove(DatabaseResourceIdentity.Key(target[..separator], target[(separator + 1)..]));
            }
            included.UnionWith(requestedDatabases
                .Select(db => DatabaseResourceIdentity.Key(db.Engine, db.DatabaseName))
                .Where(measured.Contains));
        }

        var missing = requestedDatabases
            .Where(db => !included.Contains(DatabaseResourceIdentity.Key(db.Engine, db.DatabaseName)))
            .Select(db => $"{DatabaseResourceIdentity.NormalizeEngine(db.Engine)}/{db.DatabaseName}")
            .ToArray();
        var hasContentErrors = warnings.Any(w => w.MessageKey is not ("backup.remote_unencrypted" or "backup.compression_failed"));
        var complete = usable && (record.Status is "created" or "created_remote_synced") &&
            !hasContentErrors && missing.Length == 0;
        var detail = $"status={record.Status}; integrity={record.IntegrityState}; backup_result={(complete ? "confirmed" : "incomplete_or_unconfirmed")}";
        if (missing.Length > 0)
            detail += $"; databases_unconfirmed={string.Join(",", missing)}";
        foreach (var warning in warnings)
            detail += $"; {warning.MessageKey} ({warning.Target}) {warning.Detail}";
        return new(complete, included, detail);
    }

    private static bool DatabaseSourceMatches(string? actual, string expected)
    {
        var source = actual ?? string.Empty;
        var actualSeparator = source.IndexOf(':');
        var expectedSeparator = expected.IndexOf(':');
        return actualSeparator > 0 && expectedSeparator > 0 && DatabaseResourceIdentity.Same(
            source[..actualSeparator], source[(actualSeparator + 1)..],
            expected[..expectedSeparator], expected[(expectedSeparator + 1)..]);
    }

    /// <summary>Budama sonucunun gece işi özetine yansıması: cümle + teknik satırlar + "hata var mı".</summary>
    internal sealed record PruneSummary(bool HasFailures, string Sentence, IReadOnlyList<string> TechnicalLines);

    /// <summary>
    /// Budama sonucunu ÖZETE çevirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN HATA (canlı sunucu, 28.08.2026):</b> panel şunu gösteriyordu —
    /// <c>"Son çalışma: 28.08.2026 03:09:33 · Tamamlandı · 2 site, 3 veritabanı yedeklendi,
    /// 7 eski yedek silindi."</c> Kullanıcı ise dosyaları FTP'de görüyordu. Sayı YALAN DEĞİLDİ
    /// (yalnız tam silinenler sayılır) ama uzak kopyası silinemeyen kayıtlar buraya HİÇ
    /// ulaşmıyordu: başarısızlık sadece <c>LogWarning</c>'e gidiyor, <c>hasError</c>
    /// güncellenmiyor ve özet cümlesi yalnız başarıları yazıyordu. Yani gece işi "Tamamlandı"
    /// derken hedefte yetim dosya bırakıyordu — bu deponun en pahalı hata sınıfı.
    /// </para>
    /// <para>
    /// <b>Neden statik ve saf:</b> karar (durum + cümle) 20 bağımlılıklı bir servisin içinde
    /// kalırsa ölçülemez; ölçülemeyen koruma bir sonraki düzenlemede sessizce geri alınır.
    /// </para>
    /// <para>
    /// Teknik satırlar <b>ÇEVRİLMEZ</b>: bu depoda sunucunun ürettiği serbest metin dilden
    /// bağımsız etiket=değer bilgisidir (aynı kalıp <c>BackupService</c>'te
    /// <c>scan=truncated; reason=...</c> olarak var).
    /// </para>
    /// </remarks>
    internal static PruneSummary SummarizePrune(BackupPruneResultDto pruned)
    {
        var cumle = $"{pruned.Removed} eski yedek silindi";
        if (pruned.RemoteDeleteFailed > 0)
        {
            cumle += $", {pruned.RemoteDeleteFailed} yedegin uzak kopyasi SILINEMEDI";
        }

        if (pruned.LocalDeleteFailed > 0)
        {
            cumle += $", {pruned.LocalDeleteFailed} yedegin yerel dosyasi silinemedi";
        }

        if (!pruned.HasFailures)
        {
            return new PruneSummary(false, cumle, []);
        }

        var satirlar = new List<string>
        {
            $"prune: removed={pruned.Removed}; remote_delete_failed={pruned.RemoteDeleteFailed}; "
            + $"local_delete_failed={pruned.LocalDeleteFailed}"
        };
        satirlar.AddRange(pruned.Failures.Select(basarisiz => $"prune: {basarisiz}"));

        return new PruneSummary(true, cumle, satirlar);
    }

    /// <summary>
    /// Gece yedekleme işinin durumu. <b>Budama başarısızlığı da "hata"dır</b>: yedekler alınmış
    /// olsa bile uzak kopyası silinemeyen kayıt hedefte yetim kalır ve kullanıcı bunu ancak
    /// durumdan öğrenebilir.
    /// </summary>
    internal static string ResolveBackupStatus(bool hasError, PruneSummary prune) =>
        hasError || prune.HasFailures ? "completed_with_errors" : "completed";

    private async Task RunSslRenewAsync(AutomationStateRecord state, CancellationToken cancellationToken,
        BackupExecutionApproval? approval = null)
    {
        try
        {
            var result = approval is null
                ? await sslService.RenewAllAsync(cancellationToken)
                : await sslService.RenewAllAsync(approval.CheckAsync, cancellationToken);

            // win-acme yenilemesi mail sertifikasının PEM dosyalarını değiştirdiyse Stalwart'a yeniden bas.
            var mailSync = "";
            var mailFailure = "";
            try
            {
                // The native SSL service already synchronizes mail. Preserve that result;
                // a second no-op call would discard its success and repeat store/TLS work.
                var synced = result.MailSynchronizationPerformed ? result.MailCertificate
                    : approval is null ? await mailService.SyncCertificateAsync(cancellationToken)
                    : await mailService.SyncCertificateAsync(approval.CheckAsync, cancellationToken);
                if (synced is not null)
                {
                    mailSync = $" Mail sertifikası ({synced.HostName}) güncellendi.";
                }
            }
            catch (Exception mailException) when (mailException is not OperationCanceledException &&
                approval?.Rejected(mailException) != true)
            {
                mailFailure = $"Mail certificate synchronization failed: {DescribeSslFailure(mailException)}";
                // Provider errors may include credentials; the raw exception must not reach log sinks.
                logger.LogWarning("Mail sertifikası senkronizasyonu başarısız oldu: {Details}", mailFailure);
            }

            state.LastSslRunUtc = DateTime.UtcNow;

            // OLCULEMEDIYSE "tamamlandi" DENMEZ. Aksi halde ekranda her gun basarili bir satir
            // gorunur, gercekte hicbir sertifika yenilenmez ve bu ancak 90 gun sonra tum siteler
            // ayni anda dustugunde anlasilir.
            var verificationIncomplete = !result.BindingsMeasured || result.MissingFromStoreCount > 0 ||
                result.UnverifiedCount > 0 || result.BindingDriftCount > 0 || !string.IsNullOrWhiteSpace(result.FailureReason);
            state.LastSslStatus = !result.Measured ? "failed" : verificationIncomplete ? "completed_with_errors" : "completed";
            state.LastSslDetails = result.Measured
                ? $"{result.RenewedCount} sertifika yenilendi.{mailSync}"
                : SecretRedactor.Redact(result.FailureReason) + (mailSync.Length > 0 ? "\n" + mailSync.TrimStart() : "");
            if (result.Measured)
            {
                state.LastSslDetails += $"\nCertificate verification: verified={result.VerifiedCount}; " +
                    $"missing={result.MissingFromStoreCount}; unverified={result.UnverifiedCount}; " +
                    $"binding-drift={result.BindingDriftCount}; bindings-measured={(result.BindingsMeasured ? "true" : "false")}.";
                if (!string.IsNullOrWhiteSpace(result.FailureReason))
                    state.LastSslDetails += "\n" + SecretRedactor.Redact(result.FailureReason);
            }
            if (mailFailure.Length > 0)
            {
                // Renewal can succeed while the mail server still presents its old certificate.
                if (result.Measured) state.LastSslStatus = "completed_with_errors";
                state.LastSslDetails = string.Join("\n", new[] { state.LastSslDetails, mailFailure }
                    .Where(detail => !string.IsNullOrWhiteSpace(detail)));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (approval?.Rejected(exception) != true)
        {
            state.LastSslRunUtc = DateTime.UtcNow;
            state.LastSslStatus = "failed";
            state.LastSslDetails = DescribeSslFailure(exception);
            logger.LogError("Otomatik SSL yenileme job'u başarısız oldu: {Details}", state.LastSslDetails);
        }
    }

    private static string DescribeSslFailure(Exception exception)
    {
        var diagnosis = exception is AppException app && !string.IsNullOrWhiteSpace(app.Details)
            ? $"{app.MessageKey}: {app.Details}"
            : exception.Message;
        // Never pass the original exception (including inner exceptions) to an output sink.
        return SecretRedactor.Redact(diagnosis);
    }

    /// <summary>
    /// Yedeklemenin GERCEKTEN calisip calismadigini uyariya cevirir.
    /// <para>
    /// Iki soru sorulur ve ikisi de tasinir: "son calisma nasil bitti" ve "EN SON BASARILI YEDEK NE
    /// ZAMAN". Yalnizca birincisi yeterli degildir: is hic tetiklenmemis de olabilir, o zaman durum
    /// eski bir "completed" olarak kalir ve hicbir sey yanlis gorunmez.
    /// </para>
    /// </summary>
    private async Task AddBackupHealthAlertsAsync(
        AutomationStateRecord state,
        List<(string Key, string Subject, string Body)> alerts,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!state.AutoBackupEnabled)
        {
            return;
        }

        try
        {
            var backups = await backupService.GetBackupsAsync(cancellationToken);
            // A record can describe an incomplete, missing or unreadable archive. A new
            // incremental member also cannot make its incomplete base a healthy backup.
            var lastSuccess = backups
                .Where(item => item.CreatedAtUtc != default && item.CreatedAtUtc <= now && IsHealthyBackupCopy(item))
                .Where(item => !BackupChainPlanner.Resolve(backups, item.Id, IsHealthyBackupCopy).Broken)
                .Select(item => (DateTime?)item.CreatedAtUtc)
                .Max();

            // Iki dongu kacirildiysa artik tesaduf degildir; ama cok sik araliklarda gurultu
            // yapmamak icin en az 48 saat beklenir.
            // Dönem artık aralıktan DEĞİL programdan gelir: haftalık bir programda 48 saatlik
            // eşik her hafta yanlış alarm üretirdi.
            var staleAfter = TimeSpan.FromHours(Math.Max(48, BuildSchedulePlan(state).Donem.TotalHours * 2));
            var runFailed = state.LastBackupStatus is "failed" or "completed_with_errors";
            var stale = lastSuccess is null || now - lastSuccess.Value > staleAfter;
            if (!runFailed && !stale)
            {
                return;
            }

            var lastSuccessText = lastSuccess is null
                ? "HIC BASARILI YEDEK YOK."
                : $"En son basarili yedek: {lastSuccess.Value:yyyy-MM-dd HH:mm} UTC ({(now - lastSuccess.Value).TotalDays:F1} gun once).";

            // Saklama suresinden eski tek yedek: budama korumasi onu tutuyor ama artik ESKI veriyi
            // geri yukluyorsunuz demektir. Bunu ayrica soylemek gerekir.
            var retentionNote = state.BackupRetentionDays > 0 &&
                                lastSuccess is not null &&
                                now - lastSuccess.Value > TimeSpan.FromDays(state.BackupRetentionDays)
                ? $" DIKKAT: bu yedek saklama suresinden ({state.BackupRetentionDays} gun) de eski."
                : string.Empty;

            var runText = state.LastBackupRunUtc is null
                ? "Otomatik yedekleme HIC calismadi."
                : $"Son calisma: {state.LastBackupStatus} ({state.LastBackupRunUtc.Value:yyyy-MM-dd HH:mm} UTC).";

            alerts.Add((
                "backup-health",
                "AdaPanel uyari: otomatik yedekleme saglikli degil",
                $"{runText}\r\n{state.LastBackupDetails}\r\n\r\n{lastSuccessText}{retentionNote}\r\n\r\n" +
                "Yeni yedek uretilemedigi surece eski yedekler budanmaz (kaynak bazinda koruma devrede), " +
                "ancak elinizde GUNCEL bir geri yukleme noktasi olusmaz. Yedekleme ekranindan elle bir " +
                "yedek deneyip donen hatayi giderin."));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Yedek sagligi uyari kontrolu basarisiz.");
        }
    }

    private static bool IsDue(DateTime? lastRunUtc, int intervalHours, DateTime now)
    {
        if (lastRunUtc is null)
        {
            return true;
        }

        return now >= lastRunUtc.Value.AddHours(Math.Max(1, intervalHours));
    }

    private static bool IsHealthyBackupCopy(BackupRecordDto record) =>
        !string.IsNullOrWhiteSpace(record.Id) && record.Size > 0 &&
        (record.Status is "created" or "created_remote_synced") &&
        record.IntegrityState == BackupIntegrity.Sound && BackupReplacementPolicy.HasCompleteContent(record) &&
        !record.AllCopiesMissing && !record.RemoteMissing && !record.OffsiteCopyLost &&
        ((!string.IsNullOrWhiteSpace(record.Path) && !record.LocalMissing) ||
         (!string.IsNullOrWhiteSpace(record.RemoteUri) && record.RemoteState is not (BackupPresence.None or BackupPresence.Missing)));

    /// <summary>Kayıttaki program alanlarını normalize edilmiş plana çevirir.</summary>
    private static BackupSchedulePlan BuildSchedulePlan(AutomationStateRecord state) =>
        BackupSchedule.Normalize(
            state.BackupScheduleMode,
            state.BackupIntervalHours,
            state.BackupScheduleHour,
            state.BackupScheduleMinute,
            state.BackupScheduleDayOfWeek,
            state.BackupScheduleDayOfMonth,
            state.BackupScheduleSecondHour,
            state.BackupScheduleSecondMinute);

    /// <summary>
    /// Sunucunun saat dilimi. Her çağrıda yeniden sorulur (önbelleğe alınmaz): yönetici Windows'ta
    /// saat dilimini değiştirdiğinde panelin yedek saatinin ESKİ dilimde çakılı kalmaması gerekir.
    /// </summary>
    private static TimeZoneInfo ServerTimeZone() => TimeZoneInfo.Local;

    private async Task<AutomationStateRecord> LoadAsync(CancellationToken cancellationToken)
    {
        return await fileStore.ReadAsync(StatePath, new AutomationStateRecord(), cancellationToken);
    }

    // Jobs hold an old settings snapshot while running. Merge only their results so an
    // administrator can save new settings immediately without having them reverted at completion.
    private Task SaveRunResultsAsync(AutomationStateRecord state, CancellationToken cancellationToken,
        string? expectedBackupContext = null) =>
        fileStore.UpdateAsync(StatePath, new AutomationStateRecord(), current =>
        {
            if (expectedBackupContext is not null && BackupContext(current) != expectedBackupContext)
                throw new AppException("backup.schedule_changed", 409, 409);
            current.LastBackupRunUtc = state.LastBackupRunUtc;
            current.LastBackupStatus = state.LastBackupStatus;
            current.LastBackupDetails = state.LastBackupDetails;
            current.LastSslRunUtc = state.LastSslRunUtc;
            current.LastSslStatus = state.LastSslStatus;
            current.LastSslDetails = state.LastSslDetails;
            current.LastLogCleanupRunUtc = state.LastLogCleanupRunUtc;
            current.LastLogCleanupStatus = state.LastLogCleanupStatus;
            current.LastLogCleanupDetails = state.LastLogCleanupDetails;
            current.LastUpdateCheckRunUtc = state.LastUpdateCheckRunUtc;
            current.LastUpdateCheckStatus = state.LastUpdateCheckStatus;
            current.LastUpdateCheckDetails = state.LastUpdateCheckDetails;
            current.PendingComponentUpdates = state.PendingComponentUpdates;
            return current;
        }, cancellationToken);

    private static string BackupContext(AutomationStateRecord state) => System.Text.Json.JsonSerializer.Serialize(new
    {
        state.AutoBackupEnabled, state.BackupScheduleMode, state.BackupIntervalHours,
        state.BackupScheduleHour, state.BackupScheduleMinute, state.BackupScheduleDayOfWeek,
        state.BackupScheduleDayOfMonth, state.BackupScheduleSecondHour, state.BackupScheduleSecondMinute,
        state.BackupRetentionDays, state.BackupSites, state.BackupDatabases, state.BackupMail,
        state.BackupPanelConfig, state.BackupDatabaseEngines, state.BackupTargetType,
        state.BackupRemoteUrl, state.BackupRemoteUserName, state.BackupRemoteRegion,
        state.BackupRemotePasswordProtected
    });

    private static IReadOnlyCollection<string> NormalizeEngines(IReadOnlyCollection<string> engines)
    {
        return engines
            .Where(engine => !string.IsNullOrWhiteSpace(engine))
            .Select(engine => engine.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Günlük temizliği — <b>artık kendi silme kodu yok</b>, tek kaynağa (log döndürme) delege eder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NEDEN DEĞİŞTİ (denetim Y6, ÖLÇÜLDÜ):</b> panelde günlük silen İKİ kod vardı ve
    /// <b>aynı klasörlere</b> bakıyorlardı. <c>LogRotationService</c>/<see cref="Logs.LogFileGuard"/>
    /// sıkı: yalnız doğrudan alt dosyalar, uzantı beyaz listesi, <c>ReparsePoint</c> reddi,
    /// <c>FileShare.None + DeleteOnClose</c>, özyineleme YOK. Buradaki ikinci kopya
    /// (<c>SiteLogCleanup</c>) ise korumasızdı: <c>SearchOption.AllDirectories</c> ile
    /// özyinelemeliydi ve <b>ölçüldü: özyinelemeli sayım kavşağı (junction) TAKİP EDİYOR</b>.
    /// Bir sitenin <c>W3SVC&lt;id&gt;</c> klasörü müşterinin klasörüne bağlanmışsa gece otomasyonu
    /// müşterinin dosyalarını siliyordu — <see cref="Logs.LogFileGuard"/>'ın belgesinde adıyla anılan,
    /// bu depoda BİR KEZ YAŞANMIŞ hata sınıfı. Sıkı kapı varken korumasız ikinci kopyayı yaşatmak,
    /// korumayı sıfırlamaktır: <c>SiteLogCleanup</c> SİLİNDİ.
    /// </para>
    /// <para>
    /// <b>Saklama süresi artık log döndürme politikasından gelir.</b> Otomasyon ekranındaki gün
    /// sayısı ikinci bir gerçek kaynağı olurdu; hangisinin geçerli olduğu ölçülüp AÇIKÇA yazılıyor
    /// (aşağıdaki özet satırı), böylece iki ekran çelişirse kullanıcı bunu okur.
    /// </para>
    /// </remarks>
    private async Task RunLogCleanupAsync(AutomationStateRecord state, CancellationToken cancellationToken)
    {
        var hasError = false;
        var summary = new StringBuilder();
        var ozet = string.Empty;

        try
        {
            var durum = await logRotationService.RunAsync(null, cancellationToken);
            var dondurulen = durum.Targets.Sum(hedef => hedef.RotatedFiles);
            var silinen = durum.Targets.Sum(hedef => hedef.DeletedArchives);
            var kazanc = durum.Targets.Sum(hedef => hedef.FreedBytes);
            var atlanan = durum.Targets.Sum(hedef => hedef.SkippedLockedFiles + hedef.SkippedUnsafeFiles);

            ozet =
                $"{dondurulen} log file(s) archived, {silinen} archive(s) deleted, {kazanc / (1024 * 1024)} MB reclaimed" +
                (atlanan > 0 ? $", {atlanan} file(s) skipped (locked or unsafe)" : string.Empty) +
                $". Retention: site {durum.Settings.SiteLogMaxAgeDays}d / panel {durum.Settings.PanelLogMaxAgeDays}d " +
                "(managed under Logs -> log rotation).";

            if (durum.Settings.SiteLogMaxAgeDays != state.LogRetentionDays)
            {
                // İki ekran farklı sayı gösteriyorsa GEÇERLİ olanı söyle; sessiz kalmak, kullanıcının
                // ayarladığını sandığı bir değerin hiç uygulanmadığını gizlemek olurdu.
                ozet += $" NOTE: the {state.LogRetentionDays}-day value on this screen is not applied.";
            }
        }
        catch (AdaPanel.Core.Common.AppException exception) when (
            string.Equals(exception.MessageKey, "logs.rotation_disabled", StringComparison.Ordinal))
        {
            // Politika kapalıyken silme YAPILMAZ. "Tamamlandı" demek, hiçbir şey yapmadan başarı
            // raporlamak olurdu; kullanıcı bu satırı okuyup açması gereken yeri görür.
            hasError = true;
            summary.AppendLine("Log rotation policy is disabled (Logs -> log rotation). No log file was touched.");
            logger.LogInformation("Otomatik log temizligi atlandi: log dondurme politikasi kapali.");
        }
        catch (Exception exception)
        {
            hasError = true;
            summary.AppendLine(exception.Message);
            logger.LogWarning(exception, "Otomatik log temizliği tamamlanamadı.");
        }

        state.LastLogCleanupRunUtc = DateTime.UtcNow;
        state.LastLogCleanupStatus = hasError ? "completed_with_errors" : "completed";
        state.LastLogCleanupDetails = BuildDetails(ozet, summary.ToString());
    }

    /// <summary>
    /// Kurulu bileşenlerin üstteki sürümlerini sorgular. Otomatik kurulum AÇIKSA güncellemeyi
    /// uygular — ama yalnızca güvenli sayılan bileşenlere. Veritabanı motorları, mail sunucusu ve
    /// Docker Engine dışarıda: bunlarda güncelleme veri taşıma/servis kesintisi anlamına gelir ve
    /// kararı yöneticinin vermesi gerekir (Plesk de bunları otomatik yükseltmez).
    /// </summary>
    private static readonly string[] AutoUpdateExcludedPrefixes =
    [
        "mysql", "mariadb", "postgres", "mssql", "sqlserver", "stalwart", "docker"
    ];

    internal static bool IsAutoUpdatable(string componentId)
    {
        return !AutoUpdateExcludedPrefixes.Any(prefix =>
            componentId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Kilidin dışında hesaplanan güncelleme sonucu; kilit içinde state'e yazılır.</summary>
    private sealed record UpdateCheckOutcome(string Status, string Details, int Pending);

    private static void ApplyUpdateOutcome(AutomationStateRecord state, UpdateCheckOutcome outcome)
    {
        state.LastUpdateCheckRunUtc = DateTime.UtcNow;
        state.LastUpdateCheckStatus = outcome.Status;
        state.LastUpdateCheckDetails = outcome.Details;
        state.PendingComponentUpdates = outcome.Pending;
    }

    private async Task<UpdateCheckOutcome> RunUpdateCheckAsync(bool autoApply, CancellationToken cancellationToken)
    {
        var summary = new StringBuilder();
        var hasError = false;
        var pending = new List<string>();
        var unresolvable = new List<string>();
        var applied = 0;

        try
        {
            var catalog = await componentBootstrapper.CheckForUpdatesAsync(cancellationToken);
            var outdated = catalog.Installed
                .Where(component => component.UpdateAvailable)
                .ToArray();

            unresolvable.AddRange(catalog.Installed
                .Where(component => !component.VersionCheckSupported && !component.IsSystemDetected)
                .Select(component => $"{component.DisplayName} {component.Version}"));

            var failedChecks = catalog.Installed.Where(component => component.VersionCheckFailed && !component.IsSystemDetected).ToArray();
            if (failedChecks.Length > 0)
            {
                hasError = true;
                summary.AppendLine($"Version checks failed: {string.Join(", ", failedChecks.Select(component => component.DisplayName))}");
            }

            foreach (var component in outdated)
            {
                pending.Add($"{component.DisplayName} {component.Version} -> {component.AvailableVersion}");
            }

            if (autoApply)
            {
                foreach (var component in outdated.Where(item => !item.VersionCheckFailed && IsAutoUpdatable(item.ComponentId)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // Kurulum akışı aynı bileşeni yeni sürümle yeniden kurar (upgrade yolu budur).
                        var result = await componentBootstrapper.InstallAsync(component.ComponentId, null, null, force: true, cancellationToken: cancellationToken);
                        if (string.Equals(result.Status, "Installed", StringComparison.OrdinalIgnoreCase))
                        {
                            applied++;
                            pending.Remove($"{component.DisplayName} {component.Version} -> {component.AvailableVersion}");
                        }
                        else
                        {
                            hasError = true;
                            summary.AppendLine($"{component.ComponentId}: {result.Message}");
                        }
                    }
                    catch (Exception exception)
                    {
                        hasError = true;
                        summary.AppendLine($"{component.ComponentId}: {exception.Message}");
                        logger.LogWarning(exception, "Otomatik bileşen güncellemesi başarısız: {ComponentId}", component.ComponentId);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            hasError = true;
            summary.AppendLine(exception.Message);
            logger.LogWarning(exception, "Bileşen güncelleme kontrolü tamamlanamadı.");
        }

        // DÜRÜSTLÜK: sürümü canlı sorgulanamayan bileşenler için "güncel" DEME. Onların sürümü
        // manifeste sabittir; yeni sürüm çıksa da panel göremez ve sessizce eski kalır.
        var headline = pending.Count == 0 && applied == 0
            ? hasError ? "Component version checks could not be completed." : "All checkable components are up to date."
            : $"{applied} update(s) installed, {pending.Count} pending: {string.Join(", ", pending.Take(12))}";
        if (unresolvable.Count > 0)
        {
            headline += $" | Version cannot be checked automatically ({unresolvable.Count}): {string.Join(", ", unresolvable.Take(10))}";
        }

        return new UpdateCheckOutcome(
            hasError ? "completed_with_errors" : applied > 0 ? "applied" : "completed",
            BuildDetails(headline, summary.ToString()),
            pending.Count);
    }

    private static string BuildDetails(string headline, string errors)
    {
        var trimmed = errors.Trim();
        return string.IsNullOrEmpty(trimmed) ? headline : $"{headline}\r\n{trimmed}";
    }

    private static AutomationStatusDto ToStatus(AutomationStateRecord state)
    {
        var zone = ServerTimeZone();
        var now = DateTime.UtcNow;
        var plan = BuildSchedulePlan(state);

        return new AutomationStatusDto
        {
            Settings = new AutomationSettingsDto
            {
                AutoBackupEnabled = state.AutoBackupEnabled,
                BackupScheduleMode = plan.Mode,
                BackupIntervalHours = plan.IntervalHours,
                BackupScheduleHour = plan.Hour,
                BackupScheduleMinute = plan.Minute,
                BackupScheduleDayOfWeek = plan.DayOfWeek,
                BackupScheduleDayOfMonth = plan.DayOfMonth,
                BackupScheduleSecondHour = plan.SecondHour,
                BackupScheduleSecondMinute = plan.SecondMinute,
                BackupRetentionDays = state.BackupRetentionDays,
                BackupSites = state.BackupSites,
                BackupDatabases = state.BackupDatabases,
                BackupMail = state.BackupMail,
                BackupPanelConfig = state.BackupPanelConfig,
                BackupDatabaseEngines = state.BackupDatabaseEngines,
                AutoSslRenewEnabled = state.AutoSslRenewEnabled,
                SslRenewIntervalHours = state.SslRenewIntervalHours,
                BackupTargetType = state.BackupTargetType,
                BackupRemoteUrl = state.BackupRemoteUrl,
                BackupRemoteUserName = state.BackupRemoteUserName,
                BackupRemoteRegion = state.BackupRemoteRegion,
                BackupRemotePasswordSet = !string.IsNullOrWhiteSpace(state.BackupRemotePasswordProtected),
                AlertsEnabled = state.AlertsEnabled,
                DiskUsageThresholdPercent = state.DiskUsageThresholdPercent,
                CertExpiryWarningDays = state.CertExpiryWarningDays,
                MemoryUsageThresholdPercent = state.MemoryUsageThresholdPercent,
                ServiceDownAlertsEnabled = state.ServiceDownAlertsEnabled,
                LogCleanupEnabled = state.LogCleanupEnabled,
                LogRetentionDays = state.LogRetentionDays,
                UpdateCheckEnabled = state.UpdateCheckEnabled,
                AutoApplyComponentUpdates = state.AutoApplyComponentUpdates
            },
            LastBackupRunUtc = state.LastBackupRunUtc,
            LastBackupStatus = state.LastBackupStatus,
            LastBackupDetails = state.LastBackupDetails,
            NextBackupRunUtc = state.AutoBackupEnabled
                ? BackupSchedule.SonrakiZaman(plan, state.LastBackupRunUtc, now, zone)
                : null,
            LastSslRunUtc = state.LastSslRunUtc,
            LastSslStatus = state.LastSslStatus,
            LastSslDetails = state.LastSslDetails,
            NextSslRunUtc = state.AutoSslRenewEnabled
                ? (state.LastSslRunUtc ?? DateTime.UtcNow).AddHours(Math.Max(1, state.SslRenewIntervalHours))
                : null,
            LastLogCleanupRunUtc = state.LastLogCleanupRunUtc,
            LastLogCleanupStatus = state.LastLogCleanupStatus,
            LastLogCleanupDetails = state.LastLogCleanupDetails,
            LastUpdateCheckRunUtc = state.LastUpdateCheckRunUtc,
            LastUpdateCheckStatus = state.LastUpdateCheckStatus,
            LastUpdateCheckDetails = state.LastUpdateCheckDetails,
            PendingComponentUpdates = state.PendingComponentUpdates,

            // Yedek saati SUNUCU yerel saatidir; ekran bunu tahmin ETMESİN diye dilim ve anlık
            // sunucu saati yanıtla birlikte gider.
            ServerTimeZoneId = zone.Id,
            ServerTimeZoneDisplayName = zone.DisplayName,
            ServerUtcOffsetMinutes = (int)zone.GetUtcOffset(now).TotalMinutes,
            ServerLocalTime = TimeZoneInfo.ConvertTimeFromUtc(now, zone).ToString("yyyy-MM-ddTHH:mm:ss")
        };
    }

    /// <summary>
    /// Disk doluluk ve sertifika bitiş eşiklerini kontrol eder; aşım varsa e-posta/webhook
    /// uyarısı gönderir. Aynı uyarı için 24 saat tekrar gönderilmez (cooldown dosyası).
    /// </summary>
    private async Task RunAlertChecksAsync(CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken);
        if (!state.AlertsEnabled)
        {
            return;
        }

        var emailReady = await notificationService.IsEmailConfiguredAsync(cancellationToken);
        var settings = await notificationService.GetSettingsAsync(cancellationToken);
        var webhookReady = !string.IsNullOrWhiteSpace(settings.WebhookUrl);
        if (!emailReady && !webhookReady)
        {
            return;
        }

        var cooldown = await fileStore.ReadAsync(AlertCooldownPath, new Dictionary<string, DateTime>(), cancellationToken);
        var now = DateTime.UtcNow;
        var alerts = new List<(string Key, string Subject, string Body)>();

        // Disk doluluk
        var threshold = Math.Clamp(state.DiskUsageThresholdPercent, 50, 99);
        var overview = await systemMonitoringService.GetOverviewAsync(cancellationToken);
        foreach (var disk in overview.Disks.Where(SystemMetricValidity.Disk))
        {
            if (disk.UsagePercent >= threshold)
            {
                var freeGb = disk.FreeBytes / (1024.0 * 1024 * 1024);
                alerts.Add((
                    $"disk:{disk.Name}",
                    $"AdaPanel uyari: {disk.Name} diski %{disk.UsagePercent:F0} dolu",
                    $"{disk.Name} ({disk.Label}) diski %{disk.UsagePercent:F0} doluluga ulasti. Bos alan: {freeGb:F1} GB. Esik: %{threshold}."));
            }
        }

        // RAM (bellek) doluluk
        var memoryThreshold = Math.Clamp(state.MemoryUsageThresholdPercent, 50, 99);
        if (SystemMetricValidity.Memory(overview) && overview.MemoryUsagePercent >= memoryThreshold)
        {
            alerts.Add((
                "memory",
                $"AdaPanel uyari: RAM %{overview.MemoryUsagePercent:F0} dolu",
                $"Sunucu bellegi %{overview.MemoryUsagePercent:F0} doluluga ulasti (esik: %{memoryThreshold}). Kaynak tuketen site/servisleri kontrol edin."));
        }

        // Sertifika bitisi
        var warningDays = Math.Clamp(state.CertExpiryWarningDays, 1, 90);
        var certs = await fileStore.ReadAsync(SslStatePath, Array.Empty<SslCertificateDto>(), cancellationToken);
        foreach (var cert in certs)
        {
            var daysLeft = (cert.NotAfterUtc - now).TotalDays;
            if (daysLeft <= warningDays && daysLeft >= -3)
            {
                alerts.Add((
                    $"cert:{cert.SiteName}:{cert.HostName}",
                    $"AdaPanel uyari: {cert.HostName} sertifikasi bitiyor",
                    daysLeft < 0
                        ? $"{cert.HostName} ({cert.SiteName}) sertifikasinin suresi DOLDU. Yenileyin."
                        : $"{cert.HostName} ({cert.SiteName}) sertifikasinin bitisine {daysLeft:F0} gun kaldi."));
            }
        }

        // Bilesen guncellemeleri: gunluk kontrol bekleyen guncelleme bulduysa haber ver. Eski surumde
        // kalmak en sik guvenlik acigi kaynagidir; cooldown 24 saat oldugu icin gunde bir hatirlatir.
        if (state.UpdateCheckEnabled && state.PendingComponentUpdates > 0)
        {
            alerts.Add((
                "component-updates",
                $"AdaPanel: {state.PendingComponentUpdates} bilesen icin guncelleme var",
                $"{state.LastUpdateCheckDetails}\r\n\r\nYazilim magazasi ekranindan kurabilirsiniz. " +
                "Otomatik kurulum icin Sunucu Araclari > Otomasyon > 'Guncellemeleri otomatik kur' secenegini acabilirsiniz " +
                "(veritabani motorlari, mail sunucusu ve Docker Engine guvenlik geregi haric tutulur)."));
        }

        // Panel guncellemesi: self-update GitHub'daki son surumu bilir; Danisman ekranini acmayan
        // yonetici de haberdar olsun.
        try
        {
            var panelUpdate = await selfUpdateService.CheckForUpdateAsync(forceRefresh: false, cancellationToken);
            if (panelUpdate.IsUpdateAvailable)
            {
                alerts.Add((
                    $"panel-update:{panelUpdate.LatestVersion}",
                    $"AdaPanel {panelUpdate.LatestVersion} yayinlandi",
                    $"Panelin yeni surumu ({panelUpdate.LatestVersion}) mevcut; kurulu surum {panelUpdate.CurrentVersion}. " +
                    "Guncelleme ekranindan tek tikla kurabilirsiniz."));
            }
        }
        catch (Exception exception)
        {
            logger.LogInformation(exception, "Panel guncelleme uyari kontrolu yapilamadi (ag/oran siniri).");
        }

        // Servis durumu: kurulu ama DURMUS servisler (web/mail/DB/FTP/cache) → site/posta/DB etkilenir.
        if (state.ServiceDownAlertsEnabled)
        {
            try
            {
                foreach (var service in await healthService.GetHealthAsync(cancellationToken))
                {
                    if (string.Equals(service.Status, "Stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        alerts.Add((
                            $"service:{service.Name}",
                            $"AdaPanel uyari: {service.Label} servisi DURMUS",
                            $"{service.Label} ({service.Name}) servisi calismiyor (Stopped). Baglantili site/posta/DB etkilenebilir; Sunucu Sagligi ekranindan yeniden baslatabilirsiniz."));
                    }
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Servis durumu uyari kontrolu basarisiz.");
            }
        }

        // Planli gorev hatasi (Plesk "notify on error"): son calismasi sifirdan farkli kodla biten gorevler.
        // Ozel kodlar uyari DEGIL: 267009 su an calisiyor, 267011 hic calismadi, 267045 kuyrukta.
        try
        {
            foreach (var task in await schedulerService.GetTasksAsync(cancellationToken))
            {
                if (task.LastTaskResult is not long code || code is 0 or 267009 or 267011 or 267045)
                {
                    continue;
                }

                // ToUniversalTime: System.Text.Json "Z"li ISO'yu yerel Kind'e çevirebilir; gerçek UTC'ye normalize et.
                if (task.LastRunTimeUtc is null || now - task.LastRunTimeUtc.Value.ToUniversalTime() > TimeSpan.FromDays(2))
                {
                    continue;
                }

                var target = string.IsNullOrWhiteSpace(task.Url) ? task.Command : task.Url;
                alerts.Add((
                    $"cron:{task.Name}",
                    $"AdaPanel uyari: '{task.Name}' planli gorevi basarisiz",
                    $"'{task.Name}' gorevi son calismasinda hata dondurdu (kod 0x{code:X}). Hedef: {target}. Planli Gorevler ekranindan 'Simdi calistir' ile yeniden deneyebilirsiniz."));
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Planli gorev uyari kontrolu basarisiz.");
        }

        // Yedekleme sagligi. Disk, RAM, sertifika, durmus servis ve basarisiz cron icin uyari
        // vardi ama YEDEK icin YOKTU -- panelin verebilecegi en pahali sessizlik buydu: gecelik is
        // her kaynagi ayri try/catch ile yedekler, hepsi patlasa bile "tamamlandi (hatali)" der ve
        // otomasyon ekranini acmayan yonetici bunu hicbir zaman gormez.
        await AddBackupHealthAlertsAsync(state, alerts, now, cancellationToken);

        var changed = false;
        foreach (var (key, subject, body) in alerts)
        {
            if (cooldown.TryGetValue(key, out var last) && now - last < AlertCooldown)
            {
                continue;
            }

            var delivered = false;
            if (emailReady)
            {
                try { await notificationService.SendEmailAsync(subject, body, cancellationToken); delivered = true; }
                catch (Exception ex) { logger.LogWarning(ex, "Uyari e-postasi gonderilemedi: {Key}", key); }
            }

            if (webhookReady)
            {
                try { await notificationService.SendWebhookAsync("adapanel.alert", body, cancellationToken); delivered = true; }
                catch (Exception ex) { logger.LogWarning(ex, "Uyari webhook gonderilemedi: {Key}", key); }
            }

            if (delivered)
            {
                cooldown[key] = now;
                changed = true;
            }
        }

        // Süresi geçen cooldown kayıtlarını buda.
        foreach (var stale in cooldown.Where(kv => now - kv.Value > TimeSpan.FromDays(7)).Select(kv => kv.Key).ToList())
        {
            cooldown.Remove(stale);
            changed = true;
        }

        if (changed)
        {
            await fileStore.WriteAsync(AlertCooldownPath, cooldown, cancellationToken);
        }
    }

    /// <summary>
    /// Otomatik yedeğin dokunacağı veritabanı motorları: KURULU ve YÖNETİLEBİLİR olanlar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sabit bir liste tutmak, panelin kendi kurduğu motor değiştiğinde yedeği sessizce
    /// devre dışı bırakır — nitekim bıraktı: liste <c>["mysql"]</c> idi, panel MariaDB kuruyor.
    /// </para>
    /// <para>
    /// <see cref="DatabaseEngineStatusDto.Ready"/> şartı bilinçli: motor kurulu ama panel ona
    /// bağlanamıyorsa (yönetici parolası yok) döküm zaten alınamaz. Onu listeye koymak her gece
    /// bir hata satırı üretir ve gerçek arızaları gürültüye gömerdi.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> ResolveInstalledEnginesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return SelectBackupEngines(await databaseService.GetEnginesAsync(cancellationToken));
        }
        catch (Exception exception)
        {
            // Motor listesi okunamıyorsa SESSİZ KALMA: yedek "0 veritabanı" der ve sebebi
            // görünmezdi. Boş liste dönüp özet satırında bunu söylemek, uydurma bir motor
            // listesiyle devam etmekten dürüsttür.
            logger.LogWarning(exception, "Kurulu veritabanı motorları okunamadı; otomatik veritabanı yedeği atlanıyor.");
            return [];
        }
    }

    /// <summary>Motor durumlarından yedeklenebilir olanları seçer (saf; test edilebilir).</summary>
    internal static IReadOnlyList<string> SelectBackupEngines(IEnumerable<DatabaseEngineStatusDto> engines)
    {
        return engines
            .Where(engine => engine.Installed && engine.Ready && !string.IsNullOrWhiteSpace(engine.Engine))
            .Select(engine => engine.Engine.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Sahiplik kayıtlarını siteye göre gruplar: hangi sitenin arşivine hangi veritabanı dökümü
    /// girecek.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden saf ve ayrı:</b> otomatik yedek işi on dokuz bağımlılıkla kuruluyor; bu kararı
    /// oradan test etmek hepsini taklit etmeyi gerektirirdi. Kural burada ölçülebilir hâlde durur.
    /// </para>
    /// <para>
    /// <b>Neden sahiplik kaydı, ad benzerliği DEĞİL:</b> panel hangi veritabanının hangi siteye
    /// ait olduğunu zaten biliyor. Adı siteye benzeyen bir veritabanını "onundur" diye arşive
    /// koymak, başka bir müşterinin verisini o sitenin yedeğine sızdırırdı — aynı varsayım site
    /// silmede gerçek bir veri kaybı hatasına yol açmıştı.
    /// </para>
    /// <para>
    /// Site adı BOŞ olan kayıtlar dışarıda kalır; onlar hiçbir siteye bağlı olmayan
    /// veritabanlarıdır ve kendi başlarına yedeklenmeye devam ederler.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<string, List<BackupDatabaseRef>>> ResolveDatabasesBySiteAsync(
        IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)> ownership,
        IReadOnlySet<string> existingSites,
        CancellationToken cancellationToken)
    {
        var grouped = GroupDatabasesBySite(ownership);
        if (siteDatabaseLinks is null || customerScopes is null) return grouped;

        var owned = ownership.Select(record => DatabaseResourceIdentity.Key(record.Engine, record.DatabaseName))
            .ToHashSet(StringComparer.Ordinal);
        var siteOwners = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in await siteDatabaseLinks.GetAllAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(link.Engine) || string.IsNullOrWhiteSpace(link.DatabaseName) ||
                string.IsNullOrWhiteSpace(link.SiteName)) continue;
            var siteName = link.SiteName.Trim();
            var key = DatabaseResourceIdentity.Key(link.Engine, link.DatabaseName);
            if (!existingSites.Contains(siteName) || owned.Contains(key)) continue;

            // A link is not an ownership grant. Only an explicitly measured admin site
            // can supplement the ownership catalogue with an unowned database.
            if (!siteOwners.TryGetValue(siteName, out var owner))
            {
                owner = await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
                siteOwners[siteName] = owner;
            }
            if (owner is not null) continue;
            if (!grouped.TryGetValue(siteName, out var databases)) grouped[siteName] = databases = [];
            if (databases.Any(database => DatabaseResourceIdentity.Same(database.Engine, database.DatabaseName,
                link.Engine, link.DatabaseName))) continue;
            databases.Add(new BackupDatabaseRef
            {
                Engine = DatabaseResourceIdentity.NormalizeEngine(link.Engine),
                DatabaseName = link.DatabaseName.Trim()
            });
        }
        return grouped;
    }

    internal static Dictionary<string, List<BackupDatabaseRef>> GroupDatabasesBySite(
        IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)> ownership)
    {
        return ownership
            .Where(kayit => !string.IsNullOrWhiteSpace(kayit.SiteName)
                && !string.IsNullOrWhiteSpace(kayit.Engine)
                && !string.IsNullOrWhiteSpace(kayit.DatabaseName))
            .GroupBy(kayit => kayit.SiteName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                grup => grup.Key,
                grup => grup
                    .Select(kayit => new BackupDatabaseRef
                    {
                        Engine = kayit.Engine.Trim(),
                        DatabaseName = kayit.DatabaseName.Trim()
                    })
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private string ResolveRemotePassword(AutomationStateRecord state)
    {
        if (string.IsNullOrWhiteSpace(state.BackupRemotePasswordProtected))
        {
            return string.Empty;
        }

        try
        {
            return secretProtector.Unprotect(state.BackupRemotePasswordProtected);
        }
        catch
        {
            logger.LogWarning("Uzak yedek hedefi parolası çözülemedi; uzak yükleme kimliksiz denenecek.");
            return string.Empty;
        }
    }

    /// <summary>Diske serileştirilen kalıcı durum kaydı (ayarlar + son çalışma sonuçları).</summary>
    private sealed class AutomationStateRecord
    {
        public bool AutoBackupEnabled { get; set; }

        // GERİYE DÖNÜK UYUM: bu alan eski automation.json dosyalarında YOKTUR ve "interval"
        // olarak okunur — yani saat seçmemiş kurulumlarda eski aralık davranışı aynen sürer.
        public string BackupScheduleMode { get; set; } = BackupSchedule.Interval;
        public int BackupIntervalHours { get; set; } = 24;
        public int BackupScheduleHour { get; set; } = 3;
        public int BackupScheduleMinute { get; set; }
        public int BackupScheduleDayOfWeek { get; set; } = 1;
        public int BackupScheduleDayOfMonth { get; set; } = 1;

        /// <summary>İsteğe bağlı ikinci günlük saat; null = kapalı. Sunucunun yerel saati.</summary>
        public int? BackupScheduleSecondHour { get; set; }

        public int BackupScheduleSecondMinute { get; set; }
        public int BackupRetentionDays { get; set; } = 14;
        public bool BackupSites { get; set; } = true;
        public bool BackupDatabases { get; set; }
        public bool BackupMail { get; set; }
        public bool BackupPanelConfig { get; set; }
        public IReadOnlyCollection<string> BackupDatabaseEngines { get; set; } = [];
        // VARSAYILAN ACIK. Bu, "ilk gun calisir, N gun sonra sessizce olur" sinifinin en uzun
        // fitilliydi ve KAPALI birakmak bir tercih degil, bir zaman bombasiydi:
        //
        // 1. Let's Encrypt sertifikasi 90 GUN. Kapali toggle ile panel sertifikayi kurulum gunu
        //    alir ve BIR DAHA HIC DOKUNMAZ.
        // 2. Sertifika kurulurken HTTP->HTTPS 301 yonlendirmesi KOSULSUZ aciliyor
        //    (WinAcmeSslService.ApplyHttpsRedirectAsync(..., true, ...) -- kullaniciya sorulmuyor).
        //    Yani 90. gunde ziyaretci HTTPS'e zorlanip suresi DOLMUS sertifikayla karsilasiyor:
        //    "sadece kilit gitti" degil, her sitede TAM SAYFA tarayici engeli = TOPLU KESINTI.
        // 3. Uyari yolu da kapali: AlertsEnabled varsayilan false, yani sertifika bitiyor uyarisi
        //    da gitmiyor. Advisor'daki tek isaret "info" seviyesinde.
        // 4. Arayuz zaten BUNUN ACIK OLDUGUNU SOYLUYOR: Otomasyon ekranindaki
        //    panel.automation.sslAutoHint metni ("Sertifikalar suresi dolmadan otomatik yenilenir;
        //    ek ayar gerekmez") toggle KAPALIYKEN de gorunuyor. Varsayilani acmak, panelin
        //    kullaniciya verdigi sozu tutmasidir.
        //
        // Acik olmanin maliyeti dusuk ve olculdu: yenilenecek sertifika yoksa "wacs --renew"
        // hemen doner; win-acme kurulu degilse sonuc Measured=false ile "failed" yazilir, kurulum
        // ya da panel akisi bozulmaz (bkz. RunSslRenewAsync).
        //
        // UpdateCheckEnabled ile ayni gerekce: sunucuyu ayakta tutan denetimler varsayilan ACIK,
        // KESINTI URETEBILECEK olanlar (AutoApplyComponentUpdates) varsayilan kapali kalir.
        public bool AutoSslRenewEnabled { get; set; } = true;
        public int SslRenewIntervalHours { get; set; } = 24;
        public string BackupTargetType { get; set; } = "local";
        public string BackupRemoteUrl { get; set; } = string.Empty;
        public string BackupRemoteUserName { get; set; } = string.Empty;
        public string BackupRemoteRegion { get; set; } = string.Empty;
        public string BackupRemotePasswordProtected { get; set; } = string.Empty;
        public bool AlertsEnabled { get; set; }
        public int DiskUsageThresholdPercent { get; set; } = 90;
        public int CertExpiryWarningDays { get; set; } = 14;
        public int MemoryUsageThresholdPercent { get; set; } = 90;
        public bool ServiceDownAlertsEnabled { get; set; } = true;

        public DateTime? LastBackupRunUtc { get; set; }
        public string LastBackupStatus { get; set; } = string.Empty;
        public string LastBackupDetails { get; set; } = string.Empty;
        public DateTime? LastSslRunUtc { get; set; }
        public string LastSslStatus { get; set; } = string.Empty;
        public string LastSslDetails { get; set; } = string.Empty;

        // IIS site log rotasyonu (Plesk log rotation paritesi).
        public bool LogCleanupEnabled { get; set; }
        public int LogRetentionDays { get; set; } = 30;
        public DateTime? LastLogCleanupRunUtc { get; set; }
        public string LastLogCleanupStatus { get; set; } = string.Empty;
        public string LastLogCleanupDetails { get; set; } = string.Empty;

        // Bilesen guncelleme kontrolu. UpdateCheckEnabled VARSAYILAN ACIK: sunucuyu guncel tutmanin
        // ilk sarti eski kaldigini gormek. Otomatik KURULUM ise varsayilan kapali (kesinti karari).
        public bool UpdateCheckEnabled { get; set; } = true;
        public bool AutoApplyComponentUpdates { get; set; }
        public DateTime? LastUpdateCheckRunUtc { get; set; }
        public string LastUpdateCheckStatus { get; set; } = string.Empty;
        public string LastUpdateCheckDetails { get; set; } = string.Empty;
        public int PendingComponentUpdates { get; set; }
    }
}
