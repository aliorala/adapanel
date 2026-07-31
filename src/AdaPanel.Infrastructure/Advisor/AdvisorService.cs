using System.Runtime.Versioning;
using AdaPanel.Core.Dtos.Advisor;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
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
    IPanelSecurityService panelSecurityService,
    INotificationService notificationService,
    IAntivirusService antivirusService,
    JsonFileStore fileStore) : IAdvisorService
{
    private const string SslStatePath = "config/ssl.json";

    public async Task<AdvisorReportDto> GetReportAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var items = new List<AdvisorItemDto>();

        // Panel güncellemesi (GitHub'a bakar; ağ hatası raporu bozmasın).
        try
        {
            var update = await selfUpdateService.CheckForUpdateAsync(cancellationToken);
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
        catch { /* ağ/oran sınırı: öneri atlanır */ }

        // Kurulu bileşen güncellemeleri (PHP, veritabanı, phpMyAdmin, webmail, win-acme...).
        // Günlük otomasyon kontrolünün ÖNBELLEĞİ okunur; burada ağa çıkılmaz, rapor hızlı kalır.
        try
        {
            var automation = await automationService.GetStatusAsync(cancellationToken);
            if (automation.PendingComponentUpdates > 0)
            {
                items.Add(new AdvisorItemDto
                {
                    Id = "component-updates",
                    Severity = "warning",
                    TitleKey = "advisor.components.title",
                    DetailKey = "advisor.components.detail",
                    Detail = automation.LastUpdateCheckDetails,
                    ActionSection = "software"
                });
            }
        }
        catch { }

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
        catch { }

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
        catch { }

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
        catch { }

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
        catch { }

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
        catch { }

        var ordered = items
            .OrderBy(item => SeverityRank(item.Severity))
            .ToList();

        return new AdvisorReportDto
        {
            CriticalCount = ordered.Count(item => item.Severity == "critical"),
            WarningCount = ordered.Count(item => item.Severity == "warning"),
            InfoCount = ordered.Count(item => item.Severity == "info"),
            CheckedAtUtc = now,
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
