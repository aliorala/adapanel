using System.Runtime.Versioning;
using AdaPanel.Core.Dtos.Advisor;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.Logging;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Advisor;

/// <summary>
/// Öneri motoru: panelin hâlihazırda ürettiği sinyalleri okuyup (yeni ağ/altyapı KURMADAN)
/// önem sırasına göre önerilere çevirir. Her kontrol ayrı try/catch — biri patlasa rapor tamamlanır.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdvisorService(
    ISelfUpdateService selfUpdateService,
    ISystemMonitoringService systemMonitoringService,
    IAutomationService automationService,
    IComponentBootstrapper componentBootstrapper,
    IPanelSecurityService panelSecurityService,
    INotificationService notificationService,
    IAntivirusService antivirusService,
    IPanelPreferencesService panelPreferencesService,
    JsonFileStore fileStore,
    ILogger<AdvisorService> logger) : IAdvisorService
{
    private const string SslStatePath = "config/ssl.json";

    public async Task<AdvisorReportDto> GetReportAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var items = new List<AdvisorItemDto>();
        // Çalıştırılamayan kontroller: "ölçemedim" ile "sorun yok" ekranda ayrışsın diye taşınır.
        var failed = new List<string>();

        // Panel güncellemesi (GitHub'a bakar; ağ hatası raporu bozmasın).
        try
        {
            var update = await selfUpdateService.CheckForUpdateAsync(forceRefresh: false, cancellationToken);
            if (update.IsUpdateAvailable)
            {
                items.Add(new AdvisorItemDto
                {
                    Id = "panel-update",
                    Severity = "warning",
                    TitleKey = "advisor.update.title",
                    DetailKey = "advisor.update.detail",
                    Detail = update.LatestVersion,
                    ActionSection = "updates"
                });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { failed.Add("panelUpdate"); }

        // Kurulu bileşen güncellemeleri (PHP, veritabanı, phpMyAdmin, webmail, win-acme...).
        // Güncel bileşen önbelleğini oku. Günlük otomasyonun son sonucu, elle kurulan
        // güncellemelerden sonra eski kalır ve sönmeyen bir uyarı üretir.
        try
        {
            var pending = await componentBootstrapper.GetPendingUpdateCountAsync(cancellationToken);
            if (pending > 0)
            {
                items.Add(new AdvisorItemDto
                {
                    Id = "component-updates",
                    Severity = "warning",
                    TitleKey = "advisor.components.title",
                    DetailKey = "advisor.components.detail",
                    Detail = pending.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AffectedCount = pending,
                    ActionSection = "software"
                });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Yutulan hata "sorun yok" DEĞİLDİR: rapor bunu ayrıca bildirir, aksi hâlde
            // ekranda yeşil tik ve "Her şey yolunda" görünür.
            logger.LogWarning(exception, "Danışman kontrolü çalıştırılamadı: {Check}", "components");
            failed.Add("components");
        }

        // SSL sertifikası bitişi.
        try
        {
            var certs = await fileStore.ReadAsync(SslStatePath, Array.Empty<SslCertificateDto>(), cancellationToken);
            foreach (var cert in certs)
            {
                var daysLeft = (cert.NotAfterUtc - now).TotalDays;
                var detail = $"{cert.HostName} ({cert.NotAfterUtc:yyyy-MM-dd})";
                if (daysLeft < 0)
                {
                    items.Add(new AdvisorItemDto { Id = $"ssl-expired:{cert.HostName}", Severity = "critical", TitleKey = "advisor.ssl.expired", Detail = detail, ActionSection = "sites" });
                }
                else if (daysLeft <= 14)
                {
                    items.Add(new AdvisorItemDto { Id = $"ssl-expiring:{cert.HostName}", Severity = "warning", TitleKey = "advisor.ssl.expiring", Detail = detail, ActionSection = "sites" });
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Yutulan hata "sorun yok" DEĞİLDİR: rapor bunu ayrıca bildirir, aksi hâlde
            // ekranda yeşil tik ve "Her şey yolunda" görünür.
            logger.LogWarning(exception, "Danışman kontrolü çalıştırılamadı: {Check}", "ssl");
            failed.Add("ssl");
        }

        // Disk ve bellek doluluğu.
        try
        {
            var overview = await systemMonitoringService.GetOverviewAsync(cancellationToken);
            foreach (var disk in overview.Disks)
            {
                if (disk.UsagePercent >= 95)
                {
                    items.Add(new AdvisorItemDto { Id = $"disk-critical:{disk.Name}", Severity = "critical", TitleKey = "advisor.disk.critical", Detail = $"{disk.Name} %{disk.UsagePercent:F0}", ActionSection = "monitor" });
                }
                else if (disk.UsagePercent >= 90)
                {
                    items.Add(new AdvisorItemDto { Id = $"disk-warning:{disk.Name}", Severity = "warning", TitleKey = "advisor.disk.warning", Detail = $"{disk.Name} %{disk.UsagePercent:F0}", ActionSection = "monitor" });
                }
            }

            if (overview.MemoryUsagePercent >= 90)
            {
                items.Add(new AdvisorItemDto { Id = "memory-high", Severity = "warning", TitleKey = "advisor.memory.high", Detail = $"%{overview.MemoryUsagePercent:F0}", ActionSection = "monitor" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Yutulan hata "sorun yok" DEĞİLDİR: rapor bunu ayrıca bildirir, aksi hâlde
            // ekranda yeşil tik ve "Her şey yolunda" görünür.
            logger.LogWarning(exception, "Danışman kontrolü çalıştırılamadı: {Check}", "system");
            failed.Add("system");
        }

        // Otomatik yedekleme / SSL yenileme / uyarılar.
        try
        {
            var automation = await automationService.GetStatusAsync(cancellationToken);
            if (!automation.Settings.AutoBackupEnabled)
            {
                items.Add(new AdvisorItemDto { Id = "auto-backup-off", Severity = "warning", TitleKey = "advisor.backup.off", ActionSection = "tools" });
            }

            if (!automation.Settings.AutoSslRenewEnabled)
            {
                items.Add(new AdvisorItemDto { Id = "ssl-renew-off", Severity = "info", TitleKey = "advisor.sslRenew.off", ActionSection = "tools" });
            }

            if (automation.Settings.AlertsEnabled)
            {
                var emailReady = await notificationService.IsEmailConfiguredAsync(cancellationToken);
                var settings = await notificationService.GetSettingsAsync(cancellationToken);
                var webhookReady = !string.IsNullOrWhiteSpace(settings.WebhookUrl);
                if (!emailReady && !webhookReady)
                {
                    items.Add(new AdvisorItemDto { Id = "alerts-no-channel", Severity = "info", TitleKey = "advisor.alerts.noChannel", ActionSection = "settings" });
                }
            }
            else
            {
                items.Add(new AdvisorItemDto { Id = "alerts-off", Severity = "info", TitleKey = "advisor.alerts.off", ActionSection = "tools" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Yutulan hata "sorun yok" DEĞİLDİR: rapor bunu ayrıca bildirir, aksi hâlde
            // ekranda yeşil tik ve "Her şey yolunda" görünür.
            logger.LogWarning(exception, "Danışman kontrolü çalıştırılamadı: {Check}", "automation");
            failed.Add("automation");
        }

        // Antivirüs (ClamAV): kurulu değil → öneri; kurulu ama tanımlar hiç indirilmemiş → uyarı.
        try
        {
            var antivirus = await antivirusService.GetStatusAsync(cancellationToken);
            if (!antivirus.IsInstalled)
            {
                items.Add(new AdvisorItemDto { Id = "antivirus-missing", Severity = "info", TitleKey = "advisor.antivirus.missing", ActionSection = "security" });
            }
            else if (!antivirus.DatabaseReady)
            {
                items.Add(new AdvisorItemDto { Id = "antivirus-no-definitions", Severity = "warning", TitleKey = "advisor.antivirus.noDefinitions", ActionSection = "security" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Yutulan hata "sorun yok" DEĞİLDİR: rapor bunu ayrıca bildirir, aksi hâlde
            // ekranda yeşil tik ve "Her şey yolunda" görünür.
            logger.LogWarning(exception, "Danışman kontrolü çalıştırılamadı: {Check}", "antivirus");
            failed.Add("antivirus");
        }

        // Panel adresi dışarıdan açılabilir mi.
        //
        // NEDEN BURADA: panel adresi ayarlanmadığında otomatik çözülen değer NAT arkasında
        // makinenin İÇ IP'si olur (192.168.x.x / 10.x / 172.16-31.x), hiç IPv4 yoksa makine adı.
        // Müşteriye giden parola sıfırlama, hoş geldin ve abonelikten çıkma bağlantıları bu
        // adresi taşır; müşteri açamaz, panel "gönderildi" der. Panelin bunu KENDİSİNİN fark
        // edip söylediği tek yer burasıdır.
        //
        // "warning" (kritik değil): iç ağa kurulmuş meşru bir panelde bu adres DOĞRUDUR; uyarı
        // bir arıza iddiası değil, "internetten kullanılacaksa şunu ayarla" bilgisidir. Hiçbir
        // e-posta gönderimi bu yüzden engellenmez.
        try
        {
            var preferences = await panelPreferencesService.GetAsync(cancellationToken);
            if (!preferences.ServerAddressExternallyReachable)
            {
                items.Add(new AdvisorItemDto
                {
                    Id = "panel-address-not-public",
                    Severity = "warning",
                    TitleKey = "advisor.panelAddress.title",
                    DetailKey = "advisor.panelAddress.detail",
                    Detail = preferences.ServerAddress,
                    ActionSection = "settings"
                });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Yutulan hata "sorun yok" DEĞİLDİR: rapor bunu ayrıca bildirir, aksi hâlde
            // ekranda yeşil tik ve "Her şey yolunda" görünür.
            logger.LogWarning(exception, "Danışman kontrolü çalıştırılamadı: {Check}", "panelAddress");
            failed.Add("panelAddress");
        }

        // Güvenlik ayarları: varsayılan gizli yol + IP kısıtı yok.
        try
        {
            var security = await panelSecurityService.GetSettingsAsync(cancellationToken);
            if (string.Equals(security.SecureEntryPath?.Trim('/'), "panel", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new AdvisorItemDto { Id = "secure-path-default", Severity = "warning", TitleKey = "advisor.securePath.default", ActionSection = "settings" });
            }

            if (security.AllowedIpAddresses is null || security.AllowedIpAddresses.Length == 0)
            {
                items.Add(new AdvisorItemDto { Id = "ip-allowlist-empty", Severity = "info", TitleKey = "advisor.ipAllowlist.empty", ActionSection = "settings" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Yutulan hata "sorun yok" DEĞİLDİR: rapor bunu ayrıca bildirir, aksi hâlde
            // ekranda yeşil tik ve "Her şey yolunda" görünür.
            logger.LogWarning(exception, "Danışman kontrolü çalıştırılamadı: {Check}", "security");
            failed.Add("security");
        }

        var ordered = items
            .OrderBy(item => SeverityRank(item.Severity))
            .ToList();

        return new AdvisorReportDto
        {
            CriticalCount = ordered.Count(item => item.Severity == "critical"),
            WarningCount = ordered.Count(item => item.Severity == "warning"),
            InfoCount = ordered.Count(item => item.Severity == "info"),
            CheckedAtUtc = now,
            FailedChecks = failed,
            Items = ordered
        };
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "warning" => 1,
        _ => 2
    };
}
