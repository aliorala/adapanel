using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Tam-sunucu felaket kurtarma (DR) yardımcıları: panel yedeğindeki IIS site envanteri (iis/sites.json)
/// ile sunucudaki mevcut siteler karşılaştırılır ve eksik siteler için yeniden-kurma planı üretilir.
/// Saf ve durumsuz — birim testiyle doğrulanır; IIS'e dokunan kısım BackupService'tedir.
/// </summary>
public static class IisSnapshotRestore
{
    /// <summary>Envanterde olup sunucuda olmayan siteler (ad karşılaştırması büyük/küçük harf duyarsız).</summary>
    public static IReadOnlyList<SiteDto> GetMissingSites(IEnumerable<SiteDto> snapshot, IEnumerable<string> existingSiteNames)
    {
        var existing = new HashSet<string>(existingSiteNames, StringComparer.OrdinalIgnoreCase);
        return snapshot
            .Where(site => !string.IsNullOrWhiteSpace(site.Name) && !existing.Contains(site.Name))
            .ToList();
    }

    /// <summary>
    /// Envanter kaydından yeniden-kurma planı: ilk HTTP bağlaması ana bağlama olur; kalan HTTP
    /// bağlamaları ek olarak döner. HTTPS bağlamaları BİLEREK atlanır (sertifika ister; DR sonrası
    /// SSL ekranından yeniden verilir — panel zaten HTTP→HTTPS kurulumunu SSL akışında yapar).
    /// </summary>
    public static (CreateSiteRequest Create, IReadOnlyList<SiteBindingRequest> ExtraBindings) ToProvisioningPlan(SiteDto site)
    {
        var httpBindings = site.Bindings
            .Where(binding => string.Equals(binding.Protocol, "http", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var primary = httpBindings.FirstOrDefault();
        var create = new CreateSiteRequest
        {
            Name = site.Name,
            PhysicalPath = site.PhysicalPath,
            ApplicationPoolName = site.ApplicationPoolName,
            HostName = primary?.HostName ?? string.Empty,
            IpAddress = string.IsNullOrWhiteSpace(primary?.IpAddress) ? "*" : primary!.IpAddress,
            Port = primary is { Port: > 0 } ? primary.Port : 80,
            Protocol = "http",
            AutoStart = site.AutoStart,
            // DR: kaynaklar (DB/FTP/mail) kendi yedeklerinden döner; burada YENİ kaynak üretme,
            // çalışma zamanı indirme gibi yan etkiler istenmez.
            ProvisionDefaultServices = false,
            InstallRuntimeIfMissing = false
        };

        var extras = httpBindings
            .Skip(1)
            .Select(binding => new SiteBindingRequest
            {
                Protocol = "http",
                HostName = binding.HostName,
                IpAddress = string.IsNullOrWhiteSpace(binding.IpAddress) ? "*" : binding.IpAddress,
                Port = binding.Port > 0 ? binding.Port : 80
            })
            .ToList();

        return (create, extras);
    }
}
