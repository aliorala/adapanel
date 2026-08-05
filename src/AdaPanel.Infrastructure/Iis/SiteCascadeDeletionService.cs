using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// profesyonel panel benzeri cascade site silme. Siteye bağlı kaynakları (mail domaini, veritabanı,
/// FTP, reverse proxy, disk kotası, DNS bölgesi, müşteri atamaları, git yapılandırması,
/// IIS sitesi ve dosyalar) tek işlemde temizler. Her adım best-effort'tur; biri başarısız
/// olsa da diğerleri sürer ve sonuç uyarı olarak raporlanır.
/// </summary>
public sealed class SiteCascadeDeletionService(
    IIisSiteService iisSiteService,
    IDnsService dnsService,
    IFtpService ftpService,
    IMailService mailService,
    IDatabaseService databaseService,
    ISiteDatabaseLinkService siteDatabaseLinks,
    IReverseProxyService reverseProxyService,
    IDiskQuotaService diskQuotaService,
    ICustomerScopeService customerScopeService,
    ISslService sslService,
    ISiteHostingSettingsService siteHostingSettingsService,
    IWafService wafService,
    IDirectoryProtectionService directoryProtectionService,
    ISchedulerService schedulerService,
    JsonFileStore fileStore,
    ILogger<SiteCascadeDeletionService> logger) : ISiteCascadeDeletionService
{
    private static readonly string[] DatabaseEngines = ["mysql", "mariadb", "postgresql", "mssql"];

    public async Task<SiteDeletionPlanDto> GetPlanAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        return new SiteDeletionPlanDto
        {
            SiteName = site.Name,
            PhysicalPath = site.PhysicalPath,
            ApplicationRootPath = site.ApplicationRootPath,
            HasDnsZone = await HasDnsZoneAsync(site.Name, cancellationToken),
            MailDomains = (await ResolveExistingMailDomainsAsync(site, site.Name, cancellationToken)).ToArray(),
            FtpUsers = (await ResolveFtpUsersAsync(site.Name, cancellationToken)).ToArray(),
            Databases = (await ResolveDatabasesAsync(site.Name, cancellationToken)).ToArray(),
            HasReverseProxy = await SafeBoolAsync(async () => (await reverseProxyService.GetSiteConfigurationAsync(site.Name, cancellationToken)).IsProxyEnabled),
            HasDiskQuota = await SafeBoolAsync(async () => (await diskQuotaService.GetQuotaAsync(site.Name, cancellationToken)).QuotaEnabled),
            HasGitDeployment = File.Exists(fileStore.ResolvePath(GitConfigRelativePath(site.Name))),
            CustomerAssignments = await CountCustomerAssignmentsAsync(site.Name, cancellationToken),
            ScheduledTasks = await CountScheduledTasksAsync(site, cancellationToken)
        };
    }

    public async Task<SiteDeletionResultDto> DeleteAsync(
        string siteName,
        bool deleteFiles,
        bool deleteDatabases,
        CancellationToken cancellationToken = default)
    {
        var result = new SiteDeletionResultDto { SiteName = siteName };

        SiteDto? site = null;
        try
        {
            site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        }
        catch (Exception exception)
        {
            result.Warnings.Add($"Site bilgisi okunamadı: {exception.Message}");
        }

        var physicalPath = site?.PhysicalPath ?? string.Empty;
        var appRoot = site?.ApplicationRootPath ?? string.Empty;

        // 1) Mail domainleri (hesaplar + alias dahil cascade).
        foreach (var domain in await ResolveExistingMailDomainsAsync(site, siteName, cancellationToken))
        {
            await StepAsync(result, $"Mail domaini ({domain})", () => mailService.DeleteDomainAsync(domain, cancellationToken));
        }

        // 2) Adı eşleşen veritabanları (istenirse).
        if (deleteDatabases)
        {
            foreach (var db in await ResolveDatabasesAsync(siteName, cancellationToken))
            {
                var separator = db.IndexOf(':');
                var engine = db[..separator];
                var name = db[(separator + 1)..];
                await StepAsync(result, $"Veritabanı ({db})", () => databaseService.DeleteDatabaseAsync(engine, name, cancellationToken));
            }
        }

        // 3) FTP kullanıcıları.
        foreach (var ftpUser in await ResolveFtpUsersAsync(siteName, cancellationToken))
        {
            await StepAsync(result, $"FTP kullanıcısı ({ftpUser})", () => ftpService.DeleteFtpUserAsync(ftpUser, cancellationToken));
        }

        // 4) Reverse proxy (yalnızca etkinse).
        if (await SafeBoolAsync(async () => (await reverseProxyService.GetSiteConfigurationAsync(siteName, cancellationToken)).IsProxyEnabled))
        {
            await StepAsync(result, "Reverse proxy", () => reverseProxyService.RemoveProxyAsync(siteName, cancellationToken));
        }

        // 5) Disk kotası (yalnızca etkinse).
        if (await SafeBoolAsync(async () => (await diskQuotaService.GetQuotaAsync(siteName, cancellationToken)).QuotaEnabled))
        {
            await StepAsync(result, "Disk kotası", () => diskQuotaService.RemoveQuotaAsync(siteName, cancellationToken));
        }

        // 6) DNS bölgesi.
        if (await HasDnsZoneAsync(siteName, cancellationToken))
        {
            await StepAsync(result, "DNS bölgesi", () => dnsService.DeleteZoneAsync(siteName, cancellationToken));
        }

        // 7) SSL durumu (panelin ssl.json'undaki o siteye ait sertifika kayıtları).
        await StepAsync(result, "SSL kayıtları", () => sslService.RemoveSiteAsync(siteName, cancellationToken));

        // 8) Müşteri atamaları.
        await StepAsync(result, "Müşteri atamaları", () => customerScopeService.RemoveSiteFromAllScopesAsync(siteName, cancellationToken));

        // 8b) Site↔DB bağ kayıtları (site gidince öksüz bağ kalmasın; bağlı DB'ler yukarıda 2. adımda zaten silindi).
        await StepAsync(result, "Veritabanı bağları", () => siteDatabaseLinks.RemoveSiteAsync(siteName, cancellationToken));

        // 8) Git dağıtım yapılandırması.
        TryDeleteFile(result, "Git dağıtım yapılandırması", fileStore.ResolvePath(GitConfigRelativePath(siteName)));

        // 8c) Barındırma ayarları + ARKA PLAN GÖREVLERİ (kuyruk işçisi, Laravel zamanlayıcı).
        //     Bunlar zamanlanmış görevdir: site gitse de tetiklenmeye devam ederler.
        await StepAsync(result, "Barındırma ayarları", () => siteHostingSettingsService.RemoveSiteAsync(siteName, cancellationToken));

        // 8d) Site güvenlik durumu (izolasyon / klasör izni / hız sınırı kaydı).
        await StepAsync(result, "Güvenlik durumu", () => wafService.RemoveSiteAsync(siteName, cancellationToken));

        // 8e) Korumalı dizin kayıtları (parola korumalı klasörler).
        await StepAsync(result, "Korumalı dizinler", () => directoryProtectionService.RemoveSiteAsync(siteName, cancellationToken));

        // 8f) Sitenin KLASÖRÜ İÇİNDEN çalışan zamanlanmış görevler.
        //     DOSYALAR SİLİNMEDEN ÖNCE yapılır: eşleştirme görevin komut/çalışma klasörü yoluna
        //     bakar, dolayısıyla site kökünün ne olduğunu hâlâ bilmemiz gerekir.
        if (!string.IsNullOrWhiteSpace(physicalPath))
        {
            await StepAsync(result, "Zamanlanmış görevler", async () =>
            {
                var silinen = await schedulerService.RemoveTasksUnderRootAsync(physicalPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(appRoot) &&
                    !string.Equals(appRoot, physicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    silinen += await schedulerService.RemoveTasksUnderRootAsync(appRoot, cancellationToken);
                }

                if (silinen > 0)
                {
                    result.Warnings.Add($"{silinen} zamanlanmış görev kaldırıldı (sitenin klasöründen çalışıyordu).");
                }
            });
        }

        // 9) IIS sitesi + app pool.
        await StepAsync(result, "IIS sitesi", () => iisSiteService.DeleteSiteAsync(siteName, cancellationToken));

        // 10) Dosyalar (en son; site kaldırıldıktan sonra). Uygulama kökü, fiziksel yolu
        //     da kapsıyorsa onunla birlikte gider.
        if (deleteFiles)
        {
            TryDeleteDirectory(result, "Uygulama kök dizini", appRoot);
            TryDeleteDirectory(result, "Site dosyaları", physicalPath);
        }

        return result;
    }

    // ---- Kaynak çözümleyiciler ----

    /// <summary>
    /// Sitenin klasörü içinden çalışan zamanlanmış görevleri SAYAR (silmez).
    /// <para>
    /// Silme mantığıyla aynı ölçütü kullanır: görevin komutu ya da çalışma klasörü site kökünün
    /// altındaysa o siteye aittir. Önizlemede gösterilen sayı ile gerçekte silinen sayı böylece
    /// aynı kuraldan gelir — kullanıcıya söylediğimizden farklı bir şey yapmayız.
    /// </para>
    /// </summary>
    private async Task<int> CountScheduledTasksAsync(SiteDto site, CancellationToken cancellationToken)
    {
        try
        {
            var gorevler = await schedulerService.GetTasksAsync(cancellationToken);
            var kokler = new[] { site.PhysicalPath, site.ApplicationRootPath }
                .Where(yol => !string.IsNullOrWhiteSpace(yol))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return gorevler.Count(gorev => kokler.Any(kok =>
                Scheduler.ScriptTaskPlanner.IsInsideRoot(kok, gorev.Command) ||
                Scheduler.ScriptTaskPlanner.IsInsideRoot(kok, gorev.WorkingDirectory)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sayamadıysak 0 gösteririz; önizleme bir tahmindir, silme adımı kendi ölçümünü yapar.
            logger.LogWarning(exception, "Site gorevleri sayilamadi ({SiteName}).", site.Name);
            return 0;
        }
    }

    private async Task<bool> HasDnsZoneAsync(string siteName, CancellationToken cancellationToken)
    {
        var zones = await fileStore.ReadAsync(DnsZonesPath, Array.Empty<ZoneRef>(), cancellationToken);
        return zones.Any(zone => string.Equals(zone.SiteName, siteName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Siteye/bağlamalara karşılık gelen ve mail sunucusunda GERÇEKTEN var olan domainler.</summary>
    private async Task<IReadOnlyList<string>> ResolveExistingMailDomainsAsync(SiteDto? site, string siteName, CancellationToken cancellationToken)
    {
        var candidates = BuildHostCandidates(site, siteName);
        if (candidates.Count == 0)
        {
            return [];
        }

        try
        {
            var workspace = await mailService.GetWorkspaceAsync(null, cancellationToken);
            if (!workspace.Installed)
            {
                return [];
            }

            return workspace.Domains
                .Select(domain => domain.Name.Trim().ToLowerInvariant())
                .Where(name => candidates.Contains(name))
                .Distinct()
                .ToList();
        }
        catch (Exception exception)
        {
            logger.LogInformation("Mail domainleri çözümlenemedi ({Site}): {Reason}", siteName, exception.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> ResolveFtpUsersAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            var users = await ftpService.GetFtpUsersAsync(cancellationToken);
            return users
                .Where(user => string.Equals(user.SiteName, siteName, StringComparison.OrdinalIgnoreCase))
                .Select(user => user.UserName)
                .ToList();
        }
        catch (Exception exception)
        {
            logger.LogInformation("FTP kullanıcıları çözümlenemedi ({Site}): {Reason}", siteName, exception.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> ResolveDatabasesAsync(string siteName, CancellationToken cancellationToken)
    {
        var matches = new List<string>();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { siteName.Trim(), AdaPanel.Core.Common.ResourceNaming.DatabaseNameFromSite(siteName) };

        foreach (var engine in DatabaseEngines)
        {
            try
            {
                var workspace = await databaseService.GetWorkspaceAsync(engine, cancellationToken);
                foreach (var database in workspace.Databases)
                {
                    if (wanted.Contains(database.Name.Trim()))
                    {
                        matches.Add($"{engine}:{database.Name}");
                    }
                }
            }
            catch
            {
                // Motor kurulu değil ya da erişilemiyor; atla.
            }
        }

        // GERÇEK site↔DB BAĞI (isim tahmininden bağımsız): siteye explicit bağlanmış DB'leri de ekle (Plesk gibi).
        // Böylece "karsilastir" gibi siteyle aynı adı taşımayan DB'ler de site silinince temizlenir.
        foreach (var link in await siteDatabaseLinks.GetForSiteAsync(siteName, cancellationToken))
        {
            var entry = $"{link.Engine}:{link.DatabaseName}";
            if (!matches.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                matches.Add(entry);
            }
        }

        return matches;
    }

    private async Task<int> CountCustomerAssignmentsAsync(string siteName, CancellationToken cancellationToken)
    {
        var scopes = await fileStore.ReadAsync(CustomerScopesPath, new Dictionary<string, ScopeRef>(), cancellationToken);
        return scopes.Values.Count(scope =>
            scope.Sites is not null &&
            scope.Sites.Any(site => string.Equals(site, siteName, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> BuildHostCandidates(SiteDto? site, string siteName)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = siteName.Trim().ToLowerInvariant();
        if (name.Length > 0)
        {
            candidates.Add(name);
        }

        if (site is not null)
        {
            foreach (var binding in site.Bindings)
            {
                var host = (binding.HostName ?? string.Empty).Trim().ToLowerInvariant();
                if (host.Length == 0)
                {
                    continue;
                }

                candidates.Add(host);
                if (host.StartsWith("www.", StringComparison.Ordinal))
                {
                    candidates.Add(host[4..]);
                }
            }
        }

        return candidates.ToList();
    }

    // ---- Yardımcılar ----

    private async Task StepAsync(SiteDeletionResultDto result, string label, Func<Task> action)
    {
        try
        {
            await action();
            result.Removed.Add(label);
        }
        catch (Exception exception)
        {
            // Kaynak zaten yoksa ("*_not_found") temizlenmiş say — cascade silme idempotenttir.
            if (exception.Message?.EndsWith("_not_found", StringComparison.OrdinalIgnoreCase) == true)
            {
                result.Removed.Add(label);
                return;
            }

            logger.LogWarning(exception, "Cascade silme adımı başarısız: {Label}", label);
            // GERÇEK sebebi göster: AppException.Details (ör. Stalwart CLI çıktısı) varsa onu da ekle; yoksa yalnız mesaj.
            // Eskiden sadece exception.Message (mail.operation_failed gibi anahtar) görünüyordu → neden anlaşılmıyordu.
            var reason = exception is AdaPanel.Core.Common.AppException appEx && !string.IsNullOrWhiteSpace(appEx.Details)
                ? $"{appEx.MessageKey} — {appEx.Details}"
                : exception.Message;
            result.Warnings.Add($"{label}: {reason}");
        }
    }

    private static async Task<bool> SafeBoolAsync(Func<Task<bool>> probe)
    {
        try
        {
            return await probe();
        }
        catch
        {
            return false;
        }
    }

    private void TryDeleteFile(SiteDeletionResultDto result, string label, string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                result.Removed.Add(label);
            }
        }
        catch (Exception exception)
        {
            result.Warnings.Add($"{label}: {exception.Message}");
        }
    }

    private void TryDeleteDirectory(SiteDeletionResultDto result, string label, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (!Directory.Exists(full))
            {
                return;
            }

            if (!IsSafeToDelete(full))
            {
                result.Warnings.Add($"{label}: güvenlik nedeniyle atlandı ({full})");
                return;
            }

            Directory.Delete(full, recursive: true);
            result.Removed.Add($"{label} ({full})");
        }
        catch (Exception exception)
        {
            result.Warnings.Add($"{label}: {exception.Message}");
        }
    }

    /// <summary>Sürücü kökü, sistem dizinleri ve çok sığ yolların yanlışlıkla silinmesini engeller.</summary>
    private static bool IsSafeToDelete(string fullPath)
    {
        var trimmed = fullPath.TrimEnd('\\', '/');
        var segments = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        var lower = trimmed.ToLowerInvariant();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\', '/').ToLowerInvariant();
        if (lower == windows || lower.StartsWith(windows + "\\", StringComparison.Ordinal))
        {
            return false;
        }

        string[] shallowForbidden = ["program files", "program files (x86)"];
        if (segments.Length <= 2 && shallowForbidden.Contains(segments[^1].ToLowerInvariant()))
        {
            return false;
        }

        return true;
    }

    private static string GitConfigRelativePath(string siteName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(siteName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return $"config/git/{safe}.json";
    }

    private const string DnsZonesPath = "config/dns-zones.json";
    private const string CustomerScopesPath = "config/customer-scopes.json";

    private sealed class ZoneRef
    {
        public string SiteName { get; set; } = string.Empty;
    }

    private sealed class ScopeRef
    {
        public List<string>? Sites { get; set; }
    }
}
