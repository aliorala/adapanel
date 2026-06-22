using System.Runtime.Versioning;
using System.Text;
using AdaPanel.Core.Dtos.Automation;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
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
    IDatabaseService databaseService,
    IBruteForceProtectionService bruteForceProtection,
    ISecretProtector secretProtector,
    INotificationService notificationService,
    ISystemMonitoringService systemMonitoringService,
    IConcurrencyLock concurrencyLock,
    ILogger<AutomationService> logger) : IAutomationService
{
    private const string StatePath = "config/automation.json";
    private const string AlertCooldownPath = "config/automation-alerts.json";
    private const string SslStatePath = "config/ssl.json";
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromHours(24);
    private static readonly string[] DefaultEngines = ["mysql"];

    public async Task<AutomationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return ToStatus(state);
    }

    public async Task<AutomationStatusDto> UpdateSettingsAsync(UpdateAutomationSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);

        state.AutoBackupEnabled = request.AutoBackupEnabled;
        state.BackupIntervalHours = Math.Clamp(request.BackupIntervalHours, 1, 24 * 30);
        state.BackupRetentionDays = Math.Clamp(request.BackupRetentionDays, 0, 3650);
        state.BackupSites = request.BackupSites;
        state.BackupDatabases = request.BackupDatabases;
        state.BackupMail = request.BackupMail;
        state.BackupDatabaseEngines = NormalizeEngines(request.BackupDatabaseEngines);
        state.AutoSslRenewEnabled = request.AutoSslRenewEnabled;
        state.SslRenewIntervalHours = Math.Clamp(request.SslRenewIntervalHours, 1, 24 * 30);

        // Uzak yedek hedefi: parola DPAPI ile korunur; boş gönderilirse mevcut korunur.
        var targetType = (request.BackupTargetType ?? "local").Trim().ToLowerInvariant();
        state.BackupTargetType = targetType is "ftp" or "s3" or "webdav" ? targetType : "local";
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

        state.AlertsEnabled = request.AlertsEnabled;
        state.DiskUsageThresholdPercent = Math.Clamp(request.DiskUsageThresholdPercent, 50, 99);
        state.CertExpiryWarningDays = Math.Clamp(request.CertExpiryWarningDays, 1, 90);

        await SaveAsync(state, cancellationToken);
        return ToStatus(state);
    }

    public async Task<AutomationStatusDto> RunBackupNowAsync(CancellationToken cancellationToken = default)
    {
        await concurrencyLock.RunExclusiveAsync(async () =>
        {
            var state = await LoadAsync(cancellationToken);
            await RunBackupAsync(state, cancellationToken);
            await SaveAsync(state, cancellationToken);
        }, cancellationToken);

        return await GetStatusAsync(cancellationToken);
    }

    public async Task<AutomationStatusDto> RunSslRenewNowAsync(CancellationToken cancellationToken = default)
    {
        await concurrencyLock.RunExclusiveAsync(async () =>
        {
            var state = await LoadAsync(cancellationToken);
            await RunSslRenewAsync(state, cancellationToken);
            await SaveAsync(state, cancellationToken);
        }, cancellationToken);

        return await GetStatusAsync(cancellationToken);
    }

    public async Task RunDueJobsAsync(CancellationToken cancellationToken = default)
    {
        // Süresi dolmuş brute-force engellerini temizle. Paylaşılan kilit reentrant
        // olmadığından bu çağrı exclusive bloğun dışında kalmalı.
        await bruteForceProtection.CleanupExpiredBansAsync(cancellationToken);

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

        await concurrencyLock.RunExclusiveAsync(async () =>
        {
            var state = await LoadAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var changed = false;

            if (state.AutoBackupEnabled && IsDue(state.LastBackupRunUtc, state.BackupIntervalHours, now))
            {
                await RunBackupAsync(state, cancellationToken);
                changed = true;
            }

            if (state.AutoSslRenewEnabled && IsDue(state.LastSslRunUtc, state.SslRenewIntervalHours, now))
            {
                await RunSslRenewAsync(state, cancellationToken);
                changed = true;
            }

            if (changed)
            {
                await SaveAsync(state, cancellationToken);
            }
        }, cancellationToken);
    }

    private async Task RunBackupAsync(AutomationStateRecord state, CancellationToken cancellationToken)
    {
        var summary = new StringBuilder();
        var hasError = false;
        var siteCount = 0;
        var dbCount = 0;

        try
        {
            if (state.BackupSites)
            {
                var sites = await iisSiteService.GetSitesAsync(cancellationToken);
                var remotePassword = ResolveRemotePassword(state);
                foreach (var site in sites)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await backupService.BackupSiteAsync(new BackupSiteRequest
                        {
                            SiteName = site.Name,
                            TargetType = state.BackupTargetType,
                            RemoteUrl = state.BackupRemoteUrl,
                            RemoteUserName = state.BackupRemoteUserName,
                            RemotePassword = remotePassword,
                            RemoteRegion = state.BackupRemoteRegion
                        }, cancellationToken);
                        siteCount++;
                    }
                    catch (Exception exception)
                    {
                        hasError = true;
                        summary.AppendLine($"site {site.Name}: {exception.Message}");
                        logger.LogWarning(exception, "Otomatik yedek başarısız: site {SiteName}", site.Name);
                    }
                }
            }

            if (state.BackupDatabases)
            {
                var engines = state.BackupDatabaseEngines.Count > 0 ? state.BackupDatabaseEngines : DefaultEngines;
                foreach (var engine in engines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var workspace = await databaseService.GetWorkspaceAsync(engine, cancellationToken);
                        foreach (var database in workspace.Databases)
                        {
                            try
                            {
                                await backupService.BackupDatabaseAsync(new BackupDatabaseRequest
                                {
                                    Engine = engine,
                                    DatabaseName = database.Name,
                                    TargetType = state.BackupTargetType,
                                    RemoteUrl = state.BackupRemoteUrl,
                                    RemoteUserName = state.BackupRemoteUserName,
                                    RemotePassword = ResolveRemotePassword(state),
                                    RemoteRegion = state.BackupRemoteRegion
                                }, cancellationToken);
                                dbCount++;
                            }
                            catch (Exception exception)
                            {
                                hasError = true;
                                summary.AppendLine($"db {engine}/{database.Name}: {exception.Message}");
                                logger.LogWarning(exception, "Otomatik veritabanı yedeği başarısız: {Engine}/{Database}", engine, database.Name);
                            }
                        }
                    }
                    catch (Exception exception)
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
                    await backupService.BackupMailAsync(new BackupMailRequest
                    {
                        TargetType = state.BackupTargetType,
                        RemoteUrl = state.BackupRemoteUrl,
                        RemoteUserName = state.BackupRemoteUserName,
                        RemotePassword = ResolveRemotePassword(state),
                        RemoteRegion = state.BackupRemoteRegion
                    }, cancellationToken);
                    summary.AppendLine("mail: yedeklendi");
                }
                catch (Exception exception)
                {
                    hasError = true;
                    summary.AppendLine($"mail: {exception.Message}");
                    logger.LogWarning(exception, "Otomatik mail yedeği başarısız.");
                }
            }

            var pruned = await backupService.PruneAsync(state.BackupRetentionDays, cancellationToken);

            state.LastBackupRunUtc = DateTime.UtcNow;
            state.LastBackupStatus = hasError ? "completed_with_errors" : "completed";
            state.LastBackupDetails = BuildDetails(
                $"{siteCount} site, {dbCount} veritabanı yedeklendi, {pruned} eski yedek silindi.",
                summary.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            state.LastBackupRunUtc = DateTime.UtcNow;
            state.LastBackupStatus = "failed";
            state.LastBackupDetails = exception.Message;
            logger.LogError(exception, "Otomatik yedekleme job'u başarısız oldu.");
        }
    }

    private async Task RunSslRenewAsync(AutomationStateRecord state, CancellationToken cancellationToken)
    {
        try
        {
            var result = await sslService.RenewAllAsync(cancellationToken);

            // win-acme yenilemesi mail sertifikasının PEM dosyalarını değiştirdiyse Stalwart'a yeniden bas.
            var mailSync = "";
            try
            {
                var synced = await mailService.SyncCertificateAsync(cancellationToken);
                if (synced is not null)
                {
                    mailSync = $" Mail sertifikası ({synced.HostName}) güncellendi.";
                }
            }
            catch (Exception mailException)
            {
                logger.LogWarning(mailException, "Mail sertifikası senkronizasyonu başarısız oldu.");
            }

            state.LastSslRunUtc = DateTime.UtcNow;
            state.LastSslStatus = "completed";
            state.LastSslDetails = $"{result.RenewedCount} sertifika yenilendi.{mailSync}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            state.LastSslRunUtc = DateTime.UtcNow;
            state.LastSslStatus = "failed";
            state.LastSslDetails = exception.Message;
            logger.LogError(exception, "Otomatik SSL yenileme job'u başarısız oldu.");
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

    private async Task<AutomationStateRecord> LoadAsync(CancellationToken cancellationToken)
    {
        return await fileStore.ReadAsync(StatePath, new AutomationStateRecord(), cancellationToken);
    }

    private async Task SaveAsync(AutomationStateRecord state, CancellationToken cancellationToken)
    {
        await fileStore.WriteAsync(StatePath, state, cancellationToken);
    }

    private static IReadOnlyCollection<string> NormalizeEngines(IReadOnlyCollection<string> engines)
    {
        return engines
            .Where(engine => !string.IsNullOrWhiteSpace(engine))
            .Select(engine => engine.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
    }

    private static string BuildDetails(string headline, string errors)
    {
        var trimmed = errors.Trim();
        return string.IsNullOrEmpty(trimmed) ? headline : $"{headline}\r\n{trimmed}";
    }

    private static AutomationStatusDto ToStatus(AutomationStateRecord state)
    {
        return new AutomationStatusDto
        {
            Settings = new AutomationSettingsDto
            {
                AutoBackupEnabled = state.AutoBackupEnabled,
                BackupIntervalHours = state.BackupIntervalHours,
                BackupRetentionDays = state.BackupRetentionDays,
                BackupSites = state.BackupSites,
                BackupDatabases = state.BackupDatabases,
                BackupMail = state.BackupMail,
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
                CertExpiryWarningDays = state.CertExpiryWarningDays
            },
            LastBackupRunUtc = state.LastBackupRunUtc,
            LastBackupStatus = state.LastBackupStatus,
            LastBackupDetails = state.LastBackupDetails,
            NextBackupRunUtc = state.AutoBackupEnabled
                ? (state.LastBackupRunUtc ?? DateTime.UtcNow).AddHours(Math.Max(1, state.BackupIntervalHours))
                : null,
            LastSslRunUtc = state.LastSslRunUtc,
            LastSslStatus = state.LastSslStatus,
            LastSslDetails = state.LastSslDetails,
            NextSslRunUtc = state.AutoSslRenewEnabled
                ? (state.LastSslRunUtc ?? DateTime.UtcNow).AddHours(Math.Max(1, state.SslRenewIntervalHours))
                : null
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
        foreach (var disk in overview.Disks)
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
        public int BackupIntervalHours { get; set; } = 24;
        public int BackupRetentionDays { get; set; } = 14;
        public bool BackupSites { get; set; } = true;
        public bool BackupDatabases { get; set; }
        public bool BackupMail { get; set; }
        public IReadOnlyCollection<string> BackupDatabaseEngines { get; set; } = [];
        public bool AutoSslRenewEnabled { get; set; }
        public int SslRenewIntervalHours { get; set; } = 24;
        public string BackupTargetType { get; set; } = "local";
        public string BackupRemoteUrl { get; set; } = string.Empty;
        public string BackupRemoteUserName { get; set; } = string.Empty;
        public string BackupRemoteRegion { get; set; } = string.Empty;
        public string BackupRemotePasswordProtected { get; set; } = string.Empty;
        public bool AlertsEnabled { get; set; }
        public int DiskUsageThresholdPercent { get; set; } = 90;
        public int CertExpiryWarningDays { get; set; } = 14;

        public DateTime? LastBackupRunUtc { get; set; }
        public string LastBackupStatus { get; set; } = string.Empty;
        public string LastBackupDetails { get; set; } = string.Empty;
        public DateTime? LastSslRunUtc { get; set; }
        public string LastSslStatus { get; set; } = string.Empty;
        public string LastSslDetails { get; set; } = string.Empty;
    }
}
