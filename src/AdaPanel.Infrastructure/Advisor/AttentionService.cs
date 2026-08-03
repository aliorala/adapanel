using AdaPanel.Core.Dtos.Advisor;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Advisor;

/// <summary>
/// "Dikkat" toplayıcı: menü rozetleri ve bildirim çekmecesi için TEK kaynak.
/// <para>
/// YENİ ÖLÇÜM YAPMAZ — panelin zaten ürettiği sinyalleri (Danışman, bekleyen bileşen güncellemeleri,
/// panel güncellemesi, DNS bind sağlığı, durmuş servisler) tek listede birleştirir. Sebebi:
/// 2026-07-23'te sorun panelde ÖLÇÜLÜYORDU ama kimsenin baktığı ekranda görünmüyordu; rozet bunun
/// tekrarlanmasını engeller.
/// </para>
/// Her kaynak ayrı try/catch: bir servis (ör. GitHub'a çıkan güncelleme kontrolü) patlarsa rozet
/// boş kalmaz, yalnız o kaynak <see cref="AttentionSummaryDto.FailedSources"/>'a yazılır.
/// </summary>
public sealed class AttentionService(
    IAdvisorService advisorService,
    IAutomationService automationService,
    IComponentBootstrapper componentBootstrapper,
    ISelfUpdateService selfUpdateService,
    IDnsService dnsService,
    IDomainRefreshService domainRefreshService,
    IHealthService healthService) : IAttentionService
{
    public async Task<AttentionSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<AttentionItemDto>();
        var failed = new List<string>();

        // 1) Danışman raporu: SSL bitişi, disk/bellek, güvenlik ayarları... Zaten önceliklendirilmiş
        // maddeler olduğu gibi aktarılır (tekrar yorumlamıyoruz).
        try
        {
            var report = await advisorService.GetReportAsync(cancellationToken);
            foreach (var item in report.Items)
            {
                items.Add(new AttentionItemDto
                {
                    Id = item.Id,
                    Severity = item.Severity,
                    TitleKey = item.TitleKey,
                    // Danisman ACIKLAMAYI zaten uretiyor; eskiden burada dusuruluyordu ve
                    // kullanici yalnizca sorunun adini goruyordu.
                    DescriptionKey = item.DetailKey,
                    Detail = item.Detail,
                    Section = string.IsNullOrWhiteSpace(item.ActionSection) ? "advisor" : item.ActionSection
                });
            }
        }
        catch { failed.Add("advisor"); }

        // 2) Kurulu bileşen güncellemeleri (önbellekten okunur, ağa çıkılmaz).
        // NOT: Danışman da aynı id'yi ("component-updates") üretebilir → Deduplicate tekilleştirir.
        try
        {
            // Sayacı CANLI önbellekten al: otomasyon durumu günde bir yazılır, dolayısıyla kullanıcı
            // güncellemeyi kurduktan sonra da "1 güncelleme bekliyor" demeye devam ederdi.
            var pendingUpdates = await componentBootstrapper.GetPendingUpdateCountAsync(cancellationToken);
            var automation = await automationService.GetStatusAsync(cancellationToken);
            if (pendingUpdates > 0)
            {
                items.Add(new AttentionItemDto
                {
                    Id = "component-updates",
                    Severity = "warning",
                    TitleKey = "attention.software.updates",
                    DescriptionKey = "attention.software.updates.desc",
                    Detail = string.IsNullOrWhiteSpace(automation.LastUpdateCheckDetails)
                        ? $"{pendingUpdates}"
                        : automation.LastUpdateCheckDetails,
                    Section = "software"
                });
            }
        }
        catch { failed.Add("components"); }

        // 3) Panel güncellemesi (GitHub Releases; ağ hatası tüm raporu bozmasın).
        try
        {
            var update = await selfUpdateService.CheckForUpdateAsync(forceRefresh: false, cancellationToken);
            if (update.IsUpdateAvailable)
            {
                items.Add(new AttentionItemDto
                {
                    Id = "panel-update",
                    Severity = "warning",
                    TitleKey = "attention.update.panel",
                    DescriptionKey = "attention.update.panel.desc",
                    Detail = update.LatestVersion,
                    Section = "updates"
                });
            }
        }
        catch { failed.Add("panelUpdate"); }

        // 4) DNS'in DIŞARIYA yanıt verebilirliği. Bu, 2026-07-23 kesintisinin ta kendisidir:
        // servis "Running" iken yalnız loopback'e bind kalmıştı → KRİTİK + tek-tık onarım.
        try
        {
            var dns = await dnsService.GetServerHealthAsync(cancellationToken);
            if (dns.NeedsRepair)
            {
                items.Add(new AttentionItemDto
                {
                    Id = "dns-not-publicly-bound",
                    Severity = "critical",
                    TitleKey = "attention.dns.notPubliclyBound",
                    DescriptionKey = "attention.dns.notPubliclyBound.desc",
                    Detail = dns.Details,
                    Section = "dns",
                    ActionEndpoint = "/api/v1/dns/server/repair",
                    ActionMethod = "POST"
                });
            }
        }
        catch { failed.Add("dns"); }

        // 4b) Panel güncellendiğinde YENİ varsayılanlar yalnız yeni alan adlarına uygulanır; eskiden
        // eklenmiş alan adları DMARC/DKIM/MX gibi kayıtlardan habersiz kalır. Bu eksiklik kendini
        // ancak "giden postalarım spam'e düşüyor" olarak, aylar sonra belli eder. Panel bunu KENDİSİ
        // fark etmeli: durumu değiştirmeden ölç, tek-tık tazeleme sun (onay kullanıcıda kalsın).
        try
        {
            var stale = await domainRefreshService.InspectAllAsync(cancellationToken);
            if (stale.UpdatedSites > 0)
            {
                var names = stale.Results
                    .Where(item => item.Error.Length == 0 && !item.AlreadyCurrent)
                    .Select(item => item.SiteName)
                    .Take(5)
                    .ToArray();

                items.Add(new AttentionItemDto
                {
                    Id = "domains-stale-defaults",
                    Severity = "warning",
                    TitleKey = "attention.domains.staleDefaults",
                    DescriptionKey = "attention.domains.staleDefaults.desc",
                    Detail = $"{stale.UpdatedSites}/{stale.TotalSites}: {string.Join(", ", names)}" +
                             (stale.UpdatedSites > names.Length ? " …" : string.Empty),
                    Section = "dns",
                    ActionEndpoint = "/api/v1/server-health/refresh-domains",
                    ActionMethod = "POST"
                });
            }
        }
        catch { failed.Add("domains"); }

        // 5) Durmuş Windows servisleri → tek-tık yeniden başlat.
        try
        {
            foreach (var service in await healthService.GetHealthAsync(cancellationToken))
            {
                if (!string.Equals(service.Status, "Stopped", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                items.Add(new AttentionItemDto
                {
                    Id = $"service-stopped:{service.Name}",
                    Severity = ServiceSeverity(service.Name),
                    TitleKey = "attention.service.stopped",
                    DescriptionKey = "attention.service.stopped.desc",
                    Detail = string.IsNullOrWhiteSpace(service.Label) ? service.Name : service.Label,
                    Section = "health",
                    ActionEndpoint = "/api/v1/server-health/restart",
                    ActionMethod = "POST",
                    ActionPayload = new Dictionary<string, string> { ["name"] = service.Name }
                });
            }
        }
        catch { failed.Add("services"); }

        var ordered = Prioritize(items);
        return new AttentionSummaryDto
        {
            CriticalCount = ordered.Count(item => item.Severity == "critical"),
            WarningCount = ordered.Count(item => item.Severity == "warning"),
            InfoCount = ordered.Count(item => item.Severity == "info"),
            TotalCount = ordered.Count,
            CheckedAtUtc = DateTime.UtcNow,
            SectionCounts = CountBySection(ordered),
            FailedSources = failed,
            Items = ordered
        };
    }

    /// <summary>
    /// Tekilleştirir (aynı sorunu iki kaynak da bildirebilir; ÖNEMİ YÜKSEK olan kazanır) ve
    /// önceliğe göre sıralar. Aynı önem içinde bölüm adına göre sıralanır → liste her taramada
    /// aynı sırada gelir, kullanıcı "yeni madde" ile "yer değiştirmiş madde"yi karıştırmaz.
    /// </summary>
    internal static List<AttentionItemDto> Prioritize(IEnumerable<AttentionItemDto> items)
    {
        var byId = new Dictionary<string, AttentionItemDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("n") : item.Id;
            if (!byId.TryGetValue(key, out var existing) || SeverityRank(item.Severity) < SeverityRank(existing.Severity))
            {
                byId[key] = item;
            }
        }

        return byId.Values
            .OrderBy(item => SeverityRank(item.Severity))
            .ThenBy(item => item.Section, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Menü rozetleri için bölüm başına sayaç.</summary>
    internal static Dictionary<string, int> CountBySection(IEnumerable<AttentionItemDto> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var section = string.IsNullOrWhiteSpace(item.Section) ? "advisor" : item.Section;
            counts[section] = counts.TryGetValue(section, out var current) ? current + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    /// Web/DNS çekirdek servisleri durduğunda SİTELER YAYINDA DEĞİLDİR → kritik. Diğerleri
    /// (veritabanı, mail, FTP) tek bir hizmeti etkiler → uyarı.
    /// </summary>
    internal static string ServiceSeverity(string serviceName) =>
        serviceName.ToUpperInvariant() switch
        {
            "W3SVC" or "WAS" or "DNS" => "critical",
            _ => "warning"
        };

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "warning" => 1,
        _ => 2
    };
}
