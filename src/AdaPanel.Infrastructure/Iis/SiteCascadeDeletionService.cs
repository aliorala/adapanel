using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    IDatabaseOwnershipService databaseOwnership,
    IReverseProxyService reverseProxyService,
    IDiskQuotaService diskQuotaService,
    ICustomerScopeService customerScopeService,
    ISslService sslService,
    ISiteHostingSettingsService siteHostingSettingsService,
    IWafService wafService,
    IDirectoryProtectionService directoryProtectionService,
    ISchedulerService schedulerService,
    // K9: silinecek yolun MEŞRU olduğunu kanıtlamak için panelin kendi site kökü gerekiyor.
    IPanelPreferencesService panelPreferences,
    // Panel veri kökü (yedekler + veritabanı + anahtarlar) taşınmış olabilir; yasak listesi ÖLÇÜLEN yoldan kurulur.
    IOptions<PanelRuntimeOptions> runtimeOptions,
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
            // Önizleme, GERÇEKTE SİLİNECEK kümeyi gösterir — ne fazlası ne eksiği. Adı eşleşen ama
            // sahiplik kaydı olmayan veritabanları burada YOKTUR çünkü silinmeyecekler; silme
            // sonucunda ayrıca uyarı olarak bildirilirler (SiteDeletionResultDto.Warnings).
            Databases = (await ResolveOwnedDatabasesAsync(site.Name, cancellationToken)).ToArray(),
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

        // SAHİPLİK KAYITLARI SİLMEDEN ÖNCE OKUNUR (Y1): hangi veritabanının GERÇEKTEN silindiğini
        // ancak silme denemesinden sonra biliriz, ama kaydın sahibini o noktada okumak için geç kalırız.
        var sahiplikKayitlari = await ReadOwnershipAsync(siteName, cancellationToken);
        var silinenVeritabanlari = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 2) SAHİPLİK KAYDI OLAN veritabanları (istenirse). Ad tahminiyle bulunanlar SİLİNMEZ;
        //    yalnızca uyarı olarak bildirilir — bkz. ResolveOwnedDatabasesAsync.
        if (deleteDatabases)
        {
            var sahipli = await ResolveOwnedDatabasesAsync(siteName, cancellationToken);
            foreach (var db in sahipli)
            {
                var separator = db.IndexOf(':');
                var engine = db[..separator];
                var name = db[(separator + 1)..];
                if (await StepAsync(result, $"Veritabanı ({db})", () => databaseService.DeleteDatabaseAsync(engine, name, cancellationToken)))
                {
                    // Y1: sahiplik kaydı YALNIZCA veritabanı gerçekten düştüğünde silinir.
                    silinenVeritabanlari.Add(db);
                    await TryRemoveOwnershipAsync(result, engine, name, cancellationToken);
                }
            }

            // SESSİZ KALMIYORUZ: kullanıcı eskiden bu veritabanlarının da silineceğini varsayıyordu.
            // "Silmedik ve NEDEN silmedik" demek, hem veriyi korur hem de kullanıcıyı kendi kararını
            // verebilecek durumda bırakır.
            foreach (var db in await ResolveProbableDatabasesAsync(siteName, sahipli, cancellationToken))
            {
                result.Warnings.Add(
                    $"Veritabanı ({db}): adı siteyle eşleşiyor ama panelde bu siteye ait bir sahiplik/bağ kaydı yok — SİLİNMEDİ, gerekiyorsa Veritabanları ekranından elle silin.");
            }
        }

        // 2b) MOTORDA DURAN veritabanlarının SAHİPLİĞİ KORUNUR (Y1).
        //
        // Eskiden SitesController silme çağrısından hemen sonra KOŞULSUZ `RemoveBySiteAsync`
        // çağırıyordu: "veritabanımı koru" seçilse bile kayıt gidiyor, veritabanı motorda kalıyordu.
        // Sahipsiz bir veritabanına panelden sahip atayan HİÇBİR uç yok — müşteri onu bir daha
        // göremiyor, yedekleyemiyor, bağlayamıyordu; ama kotasından düşmeye devam ediyordu.
        //
        // Artık kayıt SİLİNMEZ, yalnızca ARTIK VAR OLMAYAN site adı koparılır: sahip aynı kalır,
        // veritabanı listede/yedekte/kotada görünmeye devam eder. Site adının koparılması ayrıca
        // aynı adla açılacak YENİ bir sitenin, eski veritabanını "kendi kaydı" sanıp düşürmesini
        // engeller.
        await DetachSurvivingDatabasesAsync(result, siteName, sahiplikKayitlari, silinenVeritabanlari, cancellationToken);

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

        // SİLME POLİTİKASI IIS SİTESİ KALDIRILMADAN ÖNCE ÖLÇÜLÜR: "bu klasör başka bir siteye de
        // ait mi?" sorusunun cevabı IIS'in site listesinden gelir ve o liste birazdan değişecek.
        var silmePolitikasi = deleteFiles
            ? await BuildDeletionPolicyAsync(site, siteName, result, cancellationToken)
            : null;

        // 9) IIS sitesi + app pool.
        await StepAsync(result, "IIS sitesi", () => iisSiteService.DeleteSiteAsync(siteName, cancellationToken));

        // 10) Dosyalar (en son; site kaldırıldıktan sonra). Uygulama kökü, fiziksel yolu
        //     da kapsıyorsa onunla birlikte gider.
        if (deleteFiles && silmePolitikasi is not null)
        {
            TryDeleteDirectory(result, "Uygulama kök dizini", appRoot, silmePolitikasi);
            TryDeleteDirectory(result, "Site dosyaları", physicalPath, silmePolitikasi);
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

    /// <summary>
    /// Siteye GERÇEKTEN ait olan veritabanları — yani cascade'in düşürmeye yetkili olduğu küme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// İki OTORİTE kaynağı vardır ve ikisi de panelin kendi kaydıdır:
    /// <see cref="ISiteDatabaseLinkService"/> (açık site↔DB bağı) ve
    /// <see cref="IDatabaseOwnershipService"/> (veritabanı oluşturulurken yazılan sahiplik kaydı;
    /// site adını da taşır). Panel bu kayıtları zaten tutuyor ve silmeden hemen sonra
    /// <c>RemoveBySiteAsync</c> ile temizliyordu — yani otorite elimizdeydi ama SİLME KARARINDA
    /// kullanılmıyordu.
    /// </para>
    /// <para>
    /// AD TAHMİNİ ARTIK SİLMEZ. Eskiden "adı siteyle eşleşen" her veritabanı düşürülüyordu; bu,
    /// veritabanının kime ait olduğunu ADINDAN VARSAYMAKTIR. Bir kiracının sitesini silmesi,
    /// aynı adı taşıyan (ama başkasına ait ya da hiç kimseye bağlanmamış) bir veritabanını
    /// DROP edebiliyordu — geri alınamaz bir iş, doğrulanmamış bir varsayıma dayanıyordu.
    /// <c>SitesController.ResolveLinkableDatabasesAsync</c> doğru kuralı zaten uyguluyordu;
    /// artık silme yolu da aynı kuralı kullanıyor.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> ResolveOwnedDatabasesAsync(string siteName, CancellationToken cancellationToken)
    {
        IReadOnlyList<SiteDatabaseLinkDto> baglar = [];
        try
        {
            baglar = await siteDatabaseLinks.GetForSiteAsync(siteName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // ÖLÇEMEDİK. Okunamayan bir kaydı "sahipli" saymak veri kaybettirir, "sahipsiz" saymak
            // yalnızca fazladan bir şey bırakır. Bu depodaki kural gereği ikincisini seçiyoruz.
            logger.LogWarning(exception, "Site-DB baglari okunamadi ({Site}); bagli veritabanlari silinmeyecek.", siteName);
        }

        IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)> sahiplik = [];
        try
        {
            sahiplik = await databaseOwnership.GetAllAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Veritabani sahiplik kayitlari okunamadi ({Site}).", siteName);
        }

        return SelectDeletableDatabases(siteName, baglar, sahiplik);
    }

    /// <summary>
    /// SİLME KARARI — saf işlev, I/O yok. Testler kararı doğrudan ölçebilsin diye ayrıldı:
    /// "sahipsiz veritabanı silinmiyor" iddiası, on beş servisi taklit etmeden kanıtlanabilmeli.
    /// </summary>
    internal static IReadOnlyList<string> SelectDeletableDatabases(
        string siteName,
        IEnumerable<SiteDatabaseLinkDto> links,
        IEnumerable<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)> ownership)
    {
        var matches = new List<string>();

        void Ekle(string? engine, string? databaseName)
        {
            var entry = $"{(engine ?? string.Empty).Trim()}:{(databaseName ?? string.Empty).Trim()}";
            if (entry.Length > 1 && !matches.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                matches.Add(entry);
            }
        }

        // 1) Açık site↔DB bağı (Plesk mantığı): siteyle aynı adı taşımayan DB'ler de buradan gelir.
        foreach (var link in links)
        {
            Ekle(link.Engine, link.DatabaseName);
        }

        // 2) Sahiplik kaydı: DB oluşturulurken hangi siteye ait olduğu zaten yazılmıştı.
        foreach (var (engine, databaseName, _, kayitliSite) in ownership)
        {
            if (string.Equals((kayitliSite ?? string.Empty).Trim(), siteName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Ekle(engine, databaseName);
            }
        }

        return matches;
    }

    /// <summary>
    /// "Muhtemelen bu siteye ait" kümesi — saf işlev. Adı siteyle eşleşen ama otorite kaydı
    /// bulunmayan veritabanları; silinmezler, yalnızca bildirilirler.
    /// </summary>
    internal static IReadOnlyList<string> SelectProbableDatabases(
        string siteName,
        IEnumerable<(string Engine, string DatabaseName)> allDatabases,
        IReadOnlyCollection<string> owned)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            siteName.Trim(),
            AdaPanel.Core.Common.ResourceNaming.DatabaseNameFromSite(siteName)
        };

        return allDatabases
            .Where(item => wanted.Contains(item.DatabaseName.Trim()))
            .Select(item => $"{item.Engine}:{item.DatabaseName}")
            .Where(entry => !owned.Contains(entry, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Adı siteyle eşleşen ama SAHİPLİK KAYDI OLMAYAN veritabanları — "muhtemelen bu siteye ait".
    /// Silinmezler; kullanıcıya silme sonucunda uyarı olarak bildirilir ki elle karar verebilsin.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveProbableDatabasesAsync(
        string siteName,
        IReadOnlyCollection<string> owned,
        CancellationToken cancellationToken)
    {
        var hepsi = new List<(string Engine, string DatabaseName)>();

        foreach (var engine in DatabaseEngines)
        {
            try
            {
                var workspace = await databaseService.GetWorkspaceAsync(engine, cancellationToken);
                foreach (var database in workspace.Databases)
                {
                    hepsi.Add((engine, database.Name));
                }
            }
            catch
            {
                // Motor kurulu değil ya da erişilemiyor; atla.
            }
        }

        return SelectProbableDatabases(siteName, hepsi, owned);
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

    /// <summary>
    /// Tek adım. <b>Dönüş değeri "adım GERÇEKTEN başarılı oldu mu"dur</b> ve boşuna değildir:
    /// veritabanı sahiplik kaydı yalnızca DROP başarılıysa silinir (Y1).
    /// </summary>
    private async Task<bool> StepAsync(SiteDeletionResultDto result, string label, Func<Task> action)
    {
        try
        {
            await action();
            result.Removed.Add(label);
            return true;
        }
        catch (Exception exception)
        {
            // Kaynak zaten yoksa ("*_not_found") temizlenmiş say — cascade silme idempotenttir.
            if (exception.Message?.EndsWith("_not_found", StringComparison.OrdinalIgnoreCase) == true)
            {
                result.Removed.Add(label);
                return true;
            }

            logger.LogWarning(exception, "Cascade silme adımı başarısız: {Label}", label);
            // GERÇEK sebebi göster: AppException.Details (ör. Stalwart CLI çıktısı) varsa onu da ekle; yoksa yalnız mesaj.
            // Eskiden sadece exception.Message (mail.operation_failed gibi anahtar) görünüyordu → neden anlaşılmıyordu.
            var reason = exception is AdaPanel.Core.Common.AppException appEx && !string.IsNullOrWhiteSpace(appEx.Details)
                ? $"{appEx.MessageKey} — {appEx.Details}"
                : exception.Message;
            result.Warnings.Add($"{label}: {reason}");
            return false;
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

    /// <summary>
    /// Silme politikasını ÖLÇEREK kurar: panelin site kökleri + sistem yasakları + BAŞKA sitelerin
    /// klasörleri. Ölçülemezse <c>null</c> döner ve dosya silme HİÇ YAPILMAZ.
    /// </summary>
    /// <remarks>
    /// "Ölçemedik" ile "sorun yok" ayrımı: başka sitelerin yollarını okuyamadan silmek, Y8'in ta
    /// kendisini kör bir şekilde yapmak olurdu. Aynı varsayılan <c>SiteBuilderPathGuard</c>'da da
    /// var: ölçemediğinde REDDEDER.
    /// </remarks>
    private async Task<SiteDeletionPathPolicy?> BuildDeletionPolicyAsync(
        SiteDto? site,
        string siteName,
        SiteDeletionResultDto result,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<SiteDto> digerSiteler;
        try
        {
            digerSiteler = await iisSiteService.GetSitesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Site listesi okunamadi; {Site} icin dosya silme yapilmayacak.", siteName);
            result.Warnings.Add(
                "Site dosyaları: SİLİNMEDİ — sunucudaki diğer sitelerin klasörleri okunamadı, " +
                "klasörün başka bir siteye ait olup olmadığı doğrulanamıyor.");
            return null;
        }

        // Kendi yollarımız "başka site" değildir; aynı siteyi kendi kendine engellemek anlamsız olurdu.
        var baskaSiteYollari = digerSiteler
            .Where(diger => !string.Equals(diger.Name, siteName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(diger => new[] { diger.PhysicalPath, diger.ApplicationRootPath })
            .Where(yol => !string.IsNullOrWhiteSpace(yol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // NOT: GetSitesAsync sistem sitelerini (Default Web Site, panel araçları) listeden düşürür,
        // dolayısıyla onların yolları burada YOKTUR. Klasik olanı ("Default Web Site" →
        // C:\inetpub\wwwroot) zaten yasak kapsayıcı listesindedir.
        var siteKokleri = new List<string> { SiteDeletionPathGuard.DefaultIisSiteRoot() };
        try
        {
            var tercihler = await panelPreferences.GetAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(tercihler.DefaultSiteRoot))
            {
                siteKokleri.Add(tercihler.DefaultSiteRoot);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Tercihler okunamadıysa yalnız IIS varsayılanı kalır: kapı DARALIR, açılmaz.
            logger.LogWarning(exception, "Panel tercihleri okunamadi; site kokleri yalniz IIS varsayilanindan kuruluyor.");
        }

        // ADANMIŞLIK KANITI: klasör adı siteyle birebir aynıysa (D:\musteri.com) o klasör bu siteye
        // adanmıştır. Bu kanıt kapıyı yalnızca GENİŞLETİR; yasak listeleri ondan önce çalışır.
        var adanmisAdlar = BuildHostCandidates(site, siteName);

        return new SiteDeletionPathPolicy
        {
            KnownSiteRoots = siteKokleri,
            ForbiddenSubtrees = SiteDeletionPathGuard.SystemForbiddenSubtrees(runtimeOptions.Value.DataRoot),
            ForbiddenContainers = SiteDeletionPathGuard.SystemForbiddenContainers(),
            OtherSitePaths = baskaSiteYollari,
            DedicatedFolderNames = adanmisAdlar
        };
    }

    private void TryDeleteDirectory(SiteDeletionResultDto result, string label, string path, SiteDeletionPathPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var full = PathContainment.Normalize(path);
        if (full is null)
        {
            result.Warnings.Add($"{label}: SİLİNMEDİ — yol çözümlenemedi ({path})");
            return;
        }

        if (!Directory.Exists(full))
        {
            return;
        }

        // K9 + Y8: kara liste değil, KANIT. Geçemeyen yol silinmez ve SEBEBİ yazılır — eski
        // "güvenlik nedeniyle atlandı" mesajı yöneticiye hiçbir şey anlatmıyordu.
        var karar = SiteDeletionPathGuard.Evaluate(full, policy);
        if (karar != SiteDeletionPathVerdict.Deletable)
        {
            logger.LogWarning("Site klasoru silinmedi ({Verdict}): {Path}", karar, full);
            result.Warnings.Add($"{label}: SİLİNMEDİ — {SiteDeletionPathGuard.Describe(karar)} ({full})");
            return;
        }

        // Y7: "ne kadarı gitti" ancak ÖNCEDEN sayarsak bilinebilir.
        var oncekiDosyaSayisi = CountFilesSafely(full);

        try
        {
            Directory.Delete(full, recursive: true);
            result.Removed.Add($"{label} ({full})");
        }
        catch (Exception exception)
        {
            var klasorDuruyor = Directory.Exists(full);
            var kalanDosyaSayisi = klasorDuruyor ? CountFilesSafely(full) : 0;
            var sonuc = ClassifyDeletion(oncekiDosyaSayisi, kalanDosyaSayisi, klasorDuruyor);

            logger.LogWarning(
                exception,
                "Site klasoru silme sonucu {Outcome}: {Path} ({Before} -> {After} dosya)",
                sonuc,
                full,
                oncekiDosyaSayisi,
                kalanDosyaSayisi);

            result.Warnings.Add(DescribeDeletionFailure(
                label, full, sonuc, oncekiDosyaSayisi, kalanDosyaSayisi, exception.Message));
        }
    }

    /// <summary>Yarım kalmış silmenin GERÇEK sonucu. Saf işlev: testler kararı doğrudan ölçer.</summary>
    internal enum DirectoryDeletionOutcome
    {
        /// <summary>Klasör gitti.</summary>
        Deleted,

        /// <summary>İstisna atıldı ama dosyaların bir kısmı (çoğu zaman neredeyse hepsi) SİLİNDİ.</summary>
        PartiallyDeleted,

        /// <summary>Hiçbir şey silinmedi.</summary>
        NotDeleted
    }

    /// <summary>
    /// <b>ÖLÇÜLDÜ (denetim Y7):</b> içinde kavşak bulunan gerçek bir site ağacında
    /// <c>Directory.Delete(recursive: true)</c> istisna attı, <b>ama <c>index.php</c>,
    /// <c>uploads\photo.jpg</c> ve <c>wwwroot\a.php</c> silinmişti</b>; yalnız boş <c>wwwroot</c>
    /// kaldı. Panel bunu "Site dosyaları silinemedi" diye raporluyordu — yani gerçeğin TERSİ.
    /// Kullanıcı "silinemedi" okuyup dosyalarının durduğunu sanıyordu.
    /// </summary>
    internal static DirectoryDeletionOutcome ClassifyDeletion(int oncekiDosyaSayisi, int kalanDosyaSayisi, bool klasorDuruyor)
    {
        if (!klasorDuruyor)
        {
            return DirectoryDeletionOutcome.Deleted;
        }

        return kalanDosyaSayisi < oncekiDosyaSayisi
            ? DirectoryDeletionOutcome.PartiallyDeleted
            : DirectoryDeletionOutcome.NotDeleted;
    }

    /// <summary>Kullanıcıya ne yazılacağı — saf işlev, mesajın kendisi de test edilebilir olsun diye.</summary>
    internal static string DescribeDeletionFailure(
        string label,
        string path,
        DirectoryDeletionOutcome outcome,
        int oncekiDosyaSayisi,
        int kalanDosyaSayisi,
        string reason)
    {
        return outcome switch
        {
            DirectoryDeletionOutcome.Deleted => $"{label}: silindi ({path}) — {reason}",
            DirectoryDeletionOutcome.PartiallyDeleted =>
                $"{label}: KISMEN SİLİNDİ — {oncekiDosyaSayisi} dosyadan {oncekiDosyaSayisi - kalanDosyaSayisi} tanesi " +
                $"silindi, {kalanDosyaSayisi} tanesi kaldı ({path}). Silinenler GERİ GETİRİLEMEZ; " +
                $"kalanları elle temizlemeniz gerekir. Sebep: {reason}",
            _ => $"{label}: silinemedi, dosyalar duruyor ({path}) — {reason}"
        };
    }

    /// <summary>
    /// Klasördeki dosya sayısı. <b>Kavşak/sembolik bağlantı klasörlere GİRMEZ</b> — özyinelemeli
    /// sayımın kavşağı takip ettiği bu depoda ölçüldü (denetim Y6) ve sayım da bir okuma yoludur.
    /// Üst sınır var: silme yolunda milyonlarca dosyayı saymak işi kilitlerdi; sınır aşılırsa
    /// "kısmen silindi" kararı yine doğru çalışır (iki ölçüm de aynı sınırla yapılır).
    /// </summary>
    internal static int CountFilesSafely(string root, int limit = 20000)
    {
        var toplam = 0;
        var kuyruk = new Queue<string>();
        kuyruk.Enqueue(root);

        while (kuyruk.Count > 0 && toplam < limit)
        {
            var klasor = kuyruk.Dequeue();
            try
            {
                var bilgi = new DirectoryInfo(klasor);
                if (!bilgi.Exists || bilgi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                toplam += bilgi.EnumerateFiles().Count();
                foreach (var alt in bilgi.EnumerateDirectories())
                {
                    kuyruk.Enqueue(alt.FullName);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Okunamayan alt klasör sayıma girmez; karar iki ölçümün FARKINA bakar, mutlak sayıya değil.
            }
        }

        return Math.Min(toplam, limit);
    }

    /// <summary>Siteye ait sahiplik kayıtları — silme başlamadan önce okunur (Y1).</summary>
    private async Task<IReadOnlyList<(string Engine, string DatabaseName, Guid OwnerUserId)>> ReadOwnershipAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        try
        {
            var hepsi = await databaseOwnership.GetAllAsync(cancellationToken);
            return hepsi
                .Where(kayit => string.Equals((kayit.SiteName ?? string.Empty).Trim(), siteName.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(kayit => (kayit.Engine, kayit.DatabaseName, kayit.OwnerUserId))
                .ToList();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Veritabani sahiplik kayitlari okunamadi ({Site}).", siteName);
            return [];
        }
    }

    private async Task TryRemoveOwnershipAsync(
        SiteDeletionResultDto result,
        string engine,
        string databaseName,
        CancellationToken cancellationToken)
    {
        try
        {
            await databaseOwnership.RemoveAsync(engine, databaseName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Veritabani sahiplik kaydi silinemedi: {Engine}:{Name}", engine, databaseName);
            result.Warnings.Add($"Veritabanı sahiplik kaydı ({engine}:{databaseName}): silinemedi — {exception.Message}");
        }
    }

    /// <summary>
    /// Silinmeyen (ya da silinemeyen) veritabanlarının sahipliğini KORUR, yalnız ölü site adını koparır.
    /// </summary>
    private async Task DetachSurvivingDatabasesAsync(
        SiteDeletionResultDto result,
        string siteName,
        IReadOnlyList<(string Engine, string DatabaseName, Guid OwnerUserId)> ownership,
        IReadOnlyCollection<string> deletedDatabases,
        CancellationToken cancellationToken)
    {
        foreach (var (engine, databaseName, ownerUserId) in ownership)
        {
            if (deletedDatabases.Contains($"{engine}:{databaseName}", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                // RecordAsync idempotenttir ve kaydı SAHİBİYLE birlikte yeniden yazar; site adı boşalır.
                await databaseOwnership.RecordAsync(engine, databaseName, ownerUserId, siteName: null, cancellationToken);
                result.Warnings.Add(
                    $"Veritabanı ({engine}:{databaseName}): SİLİNMEDİ ve sahipliği KORUNDU — " +
                    $"'{siteName}' sitesiyle bağı koparıldı, Veritabanları ekranından erişilmeye devam edilebilir.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Veritabani sahipligi korunamadi: {Engine}:{Name}", engine, databaseName);
                result.Warnings.Add(
                    $"Veritabanı sahiplik kaydı ({engine}:{databaseName}): güncellenemedi — {exception.Message}");
            }
        }
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
