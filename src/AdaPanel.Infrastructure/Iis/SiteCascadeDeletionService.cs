using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
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

            // Site okunamadıysa fiziksel yolu da bilmiyoruz: dosyalar SİLİNEMEZ ve bu bir arızadır.
            // Sessiz kalmak, "site tamamen silindi" izlenimi bırakırdı.
            Kalinti(result, SiteLeftoverKinds.Site, siteName, $"site bilgisi okunamadı ({exception.Message}); klasörü/kaynakları çözümlenemedi");
        }

        var physicalPath = site?.PhysicalPath ?? string.Empty;
        var appRoot = site?.ApplicationRootPath ?? string.Empty;

        // 1) Mail domainleri (hesaplar + alias dahil cascade).
        foreach (var domain in await ResolveExistingMailDomainsAsync(site, siteName, cancellationToken))
        {
            await StepAsync(
                result,
                $"Mail domaini ({domain})",
                () => mailService.DeleteDomainAsync(domain, cancellationToken),
                SiteLeftoverKinds.MailDomain,
                domain);
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
                if (await StepAsync(
                        result,
                        $"Veritabanı ({db})",
                        () => databaseService.DeleteDatabaseAsync(engine, name, cancellationToken),
                        SiteLeftoverKinds.Database,
                        db))
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

                // BİLİNÇLİ karar: sahipliği kanıtlanmamış veritabanı düşürülmez. Sonucu başarısız
                // yapmaz ama sunucuda durduğu için ADIYLA bildirilir.
                Kalinti(
                    result,
                    SiteLeftoverKinds.Database,
                    db,
                    "adı siteyle eşleşiyor ama sahiplik/bağ kaydı yok — panel kanıtsız veritabanı düşürmez",
                    deliberate: true);
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
        await DetachSurvivingDatabasesAsync(result, siteName, sahiplikKayitlari, silinenVeritabanlari, deleteDatabases, cancellationToken);

        // 3) FTP kullanıcıları.
        foreach (var ftpUser in await ResolveFtpUsersAsync(siteName, cancellationToken))
        {
            await StepAsync(
                result,
                $"FTP kullanıcısı ({ftpUser})",
                () => ftpService.DeleteFtpUserAsync(ftpUser, cancellationToken),
                SiteLeftoverKinds.FtpUser,
                ftpUser);
        }

        // 4) Reverse proxy (yalnızca etkinse).
        if (await SafeBoolAsync(async () => (await reverseProxyService.GetSiteConfigurationAsync(siteName, cancellationToken)).IsProxyEnabled))
        {
            await StepAsync(result, "Reverse proxy", () => reverseProxyService.RemoveProxyAsync(siteName, cancellationToken), name: siteName);
        }

        // 5) Disk kotası (yalnızca etkinse).
        if (await SafeBoolAsync(async () => (await diskQuotaService.GetQuotaAsync(siteName, cancellationToken)).QuotaEnabled))
        {
            await StepAsync(result, "Disk kotası", () => diskQuotaService.RemoveQuotaAsync(siteName, cancellationToken), name: siteName);
        }

        // 6) DNS bölgesi.
        if (await HasDnsZoneAsync(siteName, cancellationToken))
        {
            await StepAsync(
                result,
                "DNS bölgesi",
                () => dnsService.DeleteZoneAsync(siteName, cancellationToken),
                SiteLeftoverKinds.DnsZone,
                siteName);
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
        await StepAsync(
            result,
            "IIS sitesi",
            () => iisSiteService.DeleteSiteAsync(siteName, cancellationToken),
            SiteLeftoverKinds.Site,
            siteName);

        // 10) Dosyalar (en son; site kaldırıldıktan sonra). Uygulama kökü, fiziksel yolu
        //     da kapsıyorsa onunla birlikte gider.
        if (deleteFiles && silmePolitikasi is not null)
        {
            TryDeleteDirectory(result, "Uygulama kök dizini", appRoot, silmePolitikasi);
            TryDeleteDirectory(result, "Site dosyaları", physicalPath, silmePolitikasi);
        }
        else if (!deleteFiles)
        {
            // BİLİNÇLİ: kullanıcı "dosyaları koru" dedi. Sonuç başarısız değildir ama dosyalar
            // sunucuda duruyor ve artık onları hangi sitenin bıraktığını gösteren bir kayıt yok —
            // yol ADIYLA bildirilir.
            foreach (var yol in new[] { appRoot, physicalPath }
                .Where(aday => !string.IsNullOrWhiteSpace(aday))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Kalinti(result, SiteLeftoverKinds.Files, yol, "dosyaları silme seçilmedi — klasör sunucuda bırakıldı", deliberate: true);
            }
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
    /// <param name="kind">Kalıntı olarak bildirilecek kaynak tipi (bkz. <see cref="SiteLeftoverKinds"/>).</param>
    /// <param name="name">Kaynağın ADI — başarısızlıkta yöneticiye gösterilecek olan budur.</param>
    /// <remarks>
    /// <c>internal</c>: bu metot "yıkıcı adım başarısız olunca ne olur" kararının TAMAMINI taşıyor
    /// (kaynak zaten yok mu, uyarı ne yazacak, kalıntı listesine ne girecek). Testler bu kararı
    /// on dokuz servisi taklit etmeden ölçebilmeli — aksi hâlde en kritik kablo test edilmeden kalır
    /// (bir mutasyon turunda tam olarak bu oldu: kalıntı satırı silindi ve hiçbir test kırılmadı).
    /// </remarks>
    internal async Task<bool> StepAsync(
        SiteDeletionResultDto result,
        string label,
        Func<Task> action,
        string kind = SiteLeftoverKinds.Other,
        string? name = null)
    {
        try
        {
            await action();
            result.Removed.Add(label);
            return true;
        }
        catch (Exception exception)
        {
            if (KaynakZatenYokMu(exception))
            {
                result.Removed.Add(label);
                return true;
            }

            logger.LogWarning(exception, "Cascade silme adımı başarısız: {Label}", label);
            // GERÇEK sebebi göster: AppException.Details (ör. Stalwart CLI çıktısı) varsa onu da ekle; yoksa yalnız mesaj.
            // Eskiden sadece exception.Message (mail.operation_failed gibi anahtar) görünüyordu → neden anlaşılmıyordu.
            var reason = exception is AppException appEx && !string.IsNullOrWhiteSpace(appEx.Details)
                ? $"{appEx.MessageKey} — {appEx.Details}"
                : exception.Message;
            result.Warnings.Add($"{label}: {reason}");
            Kalinti(result, kind, name ?? label, reason);
            return false;
        }
    }

    /// <summary>
    /// Yıkıcı bir adımın <b>başarı sayılabileceği tek durum</b>: kaynak zaten yoktu.
    /// Saf işlev — kararın kendisi test edilebilsin diye ayrıldı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ESKİ KURAL DİZGEYE BAKIYORDU: istisnanın mesajı <c>_not_found</c> ile bitiyorsa kaynak
    /// "kaldırıldı" sayılıyordu. Depoda 35 <c>*_not_found</c> anahtarı var ve bir kısmı kaynağın
    /// yokluğunu DEĞİL <b>aracın/yolun</b> yokluğunu bildiriyor: <c>php.executable_not_found</c>,
    /// <c>nginx.executable_not_found</c>, <c>cron.script_not_found</c>, <c>quota.path_not_found</c>,
    /// <c>git.path_not_found</c>. Böyle bir hata yıkıcı bir adımda yüzeye çıksaydı panel kaynağı
    /// silinmiş sayardı — kaynak duruyor, panel "kaldırıldı" diyor.
    /// </para>
    /// <para>
    /// DÜRÜSTLÜK: bugün bu yolda ulaşılabilir bir örnek BULUNAMADI (cascade'in çağırdığı silme
    /// uçları — IIS, FTP, DNS, mail, veritabanı — kaynağı bulamayınca zaten sessizce dönüyor,
    /// istisna atmıyor). Yani istismarı kanıtlanmış değil; <b>tasarım</b> kırılgandı ve kural
    /// artık dizgeye değil <see cref="AppException.ResourceAlreadyGone"/> bayrağına bakıyor.
    /// Bayrağı yalnız fırlatan taraf koyar, çünkü kaynağın var olup olmadığını yalnız o bilir.
    /// </para>
    /// </remarks>
    internal static bool KaynakZatenYokMu(Exception exception)
        => exception is AppException { ResourceAlreadyGone: true };

    /// <summary>
    /// Sunucuda kalan bir kaynağı sonuca YAZAR. Tek giriş noktası olması bilerek: kalıntının
    /// sessizce eklendiği ikinci bir yol açılırsa "ne kaldı" listesi yine eksik olur.
    /// </summary>
    private static void Kalinti(SiteDeletionResultDto result, string kind, string name, string reason, bool deliberate = false)
    {
        result.Leftovers.Add(new SiteLeftoverDto
        {
            Kind = kind,
            Name = name,
            Reason = reason,
            Deliberate = deliberate
        });
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
            Kalinti(result, SiteLeftoverKinds.Files, fullPath, exception.Message);
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

            // Kapı bilerek kapandı ama SONUÇ yine de "site tamamen silindi" değildir: dosyalar duruyor.
            foreach (var yol in new[] { site?.ApplicationRootPath, site?.PhysicalPath }
                .Where(aday => !string.IsNullOrWhiteSpace(aday))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Kalinti(
                    result,
                    SiteLeftoverKinds.Files,
                    yol!,
                    "diğer sitelerin klasörleri okunamadığı için silme yapılmadı (yanlış klasörü silmemek için)");
            }

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

    /// <remarks>
    /// <c>internal</c>: Y7'nin merkezi burasıdır — "yarım kaldı" ölçümü ve kalıntı kaydı. Gerçek
    /// dosyalarla, gerçek bir kilitle ölçülebilmeli. Kapının kendisi (<see cref="SiteDeletionPathGuard"/>)
    /// bu görünürlükten etkilenmez: politika hâlâ ÇAĞIRAN tarafından ölçülerek kurulur.
    /// </remarks>
    internal void TryDeleteDirectory(SiteDeletionResultDto result, string label, string path, SiteDeletionPathPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var full = PathContainment.Normalize(path);
        if (full is null)
        {
            result.Warnings.Add($"{label}: SİLİNMEDİ — yol çözümlenemedi ({path})");
            Kalinti(result, SiteLeftoverKinds.Files, path, "yol çözümlenemedi, klasör silinmedi");
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

            // Kapı BİLEREK kapandı (yanlış klasörü silmemek için) ama klasör duruyor: "bilinçli"
            // DEĞİL diye işaretlenir, çünkü kullanıcı bu klasörün silinmesini istemişti ve
            // silinmedi — birinin gidip bakması gerekiyor.
            Kalinti(result, SiteLeftoverKinds.Files, full, SiteDeletionPathGuard.Describe(karar));
            return;
        }

        // Y7: "ne kadarı gitti" ancak ÖNCEDEN sayarsak bilinebilir.
        var oncekiOlcum = CountFilesSafely(full);

        try
        {
            Directory.Delete(full, recursive: true);
            result.Removed.Add($"{label} ({full})");
        }
        catch (Exception exception)
        {
            var klasorDuruyor = Directory.Exists(full);

            // Klasör gitmişse SAYMAYA GEREK YOK ve sayılamaz da: "0 dosya, TAM ölçüm" burada
            // ölçülmüş bir olgudur (var olmayan klasörde dosya olamaz), varsayım değil.
            var kalanOlcum = klasorDuruyor ? CountFilesSafely(full) : new FileCountMeasurement(0, true, string.Empty);
            var sonuc = ClassifyDeletion(oncekiOlcum, kalanOlcum, klasorDuruyor);

            logger.LogWarning(
                exception,
                "Site klasoru silme sonucu {Outcome}: {Path} ({Before} -> {After} dosya, olculdu={BeforeMeasured}/{AfterMeasured})",
                sonuc,
                full,
                oncekiOlcum.FileCount,
                kalanOlcum.FileCount,
                oncekiOlcum.Measured,
                kalanOlcum.Measured);

            KlasorSonucunuYaz(result, label, full, sonuc, oncekiOlcum, kalanOlcum, exception.Message);
        }
    }

    /// <summary>
    /// Yarım kalan bir klasör silmesinin SONUÇ NESNESİNE nasıl yazılacağı: uyarı cümlesi + kalıntı
    /// kaydı + "ölçülebildi mi" bayrağı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>internal</c> ve servisten BAĞIMSIZ (yalnız DTO alır): bu üç yazının birlikte yapılması
    /// kararın kendisidir ve on dokuz servisi taklit etmeden ölçülebilmeli. Ayrılmasının somut
    /// sebebi <see cref="DirectoryDeletionOutcome.MeasurementIncomplete"/>: bayrağı kurup arayüze
    /// bağlamamak bu depoda tekrar eden bir hata sınıfı ("yazılmış ama bağlanmamış") ve tam olarak
    /// bu satırların testsiz kalması demekti.
    /// </para>
    /// </remarks>
    internal static void KlasorSonucunuYaz(
        SiteDeletionResultDto result,
        string label,
        string path,
        DirectoryDeletionOutcome outcome,
        FileCountMeasurement oncesi,
        FileCountMeasurement kalan,
        string reason)
    {
        result.Warnings.Add(DescribeDeletionFailure(label, path, outcome, oncesi, kalan, reason));

        if (outcome == DirectoryDeletionOutcome.MeasurementIncomplete)
        {
            // BAYRAK YALNIZ FALSE'A ÇEVRİLİR, geri true yapılmaz: bu metot bir silmede İKİ KEZ
            // çalışır (uygulama kökü + fiziksel yol). Birinde ölçüm kesildiyse sonucun tamamı
            // artık "ölçüldü" değildir; ikinci çağrının onu temizlemesi, kullanıcıya kesin cümle
            // kurmanın sessiz yolu olurdu.
            result.FilesDeletionMeasured = false;
            if (string.IsNullOrEmpty(result.FilesDeletionTruncatedReasonKey))
            {
                result.FilesDeletionTruncatedReasonKey = KesilmeSebebi(oncesi, kalan);
            }
        }

        // "Klasör gitti ama istisna atıldı" bir kalıntı DEĞİLDİR: diskte bir şey kalmadı.
        if (outcome == DirectoryDeletionOutcome.Deleted)
        {
            return;
        }

        Kalinti(
            result,
            SiteLeftoverKinds.Files,
            path,
            outcome switch
            {
                // Kalan sayı burada ÇIPLAK basılabilir: "kısmen silindi" kararı iki dalın ikisinde de
                // kalan ölçümün TAM olmasını şart koşuyor (bkz. ClassifyDeletion), yani bu sayı alt
                // sınır değil kesin değerdir.
                DirectoryDeletionOutcome.PartiallyDeleted =>
                    $"KISMEN silindi — {kalan.FileCount} dosya kaldı ({reason})",

                // KALINTI OLARAK YAZILIR ama SEBEBİ "silinemedi" DEMEZ: klasörün durduğu
                // gözlemlendi (bu dal yalnız orada oluşur), içindekilerin akıbeti ölçülemedi.
                DirectoryDeletionOutcome.MeasurementIncomplete =>
                    $"klasör duruyor; içindekilerin ne kadarının silindiği ÖLÇÜLEMEDİ — elle kontrol edin ({reason})",

                _ => $"silinemedi, dosyalar duruyor ({reason})"
            });
    }

    /// <summary>Yarım kalmış silmenin GERÇEK sonucu. Saf işlev: testler kararı doğrudan ölçer.</summary>
    internal enum DirectoryDeletionOutcome
    {
        /// <summary>Klasör gitti.</summary>
        Deleted,

        /// <summary>İstisna atıldı ama dosyaların bir kısmı (çoğu zaman neredeyse hepsi) SİLİNDİ.</summary>
        PartiallyDeleted,

        /// <summary>Hiçbir şey silinmedi.</summary>
        NotDeleted,

        /// <summary>
        /// <b>BİLMİYORUZ:</b> klasör duruyor ama içindekilerin ne kadarının silindiği ÖLÇÜLEMEDİ.
        /// </summary>
        /// <remarks>
        /// Yukarıdaki iki durumdan HANGİSİNE düşüldüğü belirsizdir; ikisinden birine yuvarlamak,
        /// bu sınıflandırmanın kapatmak için var olduğu yalanın (bkz. <see cref="ClassifyDeletion"/>)
        /// tavanın üstüne taşınmış hâlidir.
        /// </remarks>
        MeasurementIncomplete
    }

    /// <summary>
    /// Bir klasördeki dosya sayısı — VE o sayının TAM ölçüm mü yoksa ALT SINIR mı olduğu.
    /// </summary>
    /// <remarks>
    /// <b>Kalıp uydurulmadı, panelin disk kotasından kopyalandı:</b> aynı soru orada
    /// <c>DiskUsage(UsageMb, Measured, TruncatedReasonKey)</c> ile cevaplanıyor. Düz bir <c>int</c>,
    /// kaynaktaki (<see cref="SafeDirectoryWalkResult.Truncated"/>) "bu sayı bir ALT SINIR" bilgisini
    /// yolda düşürüyordu.
    /// </remarks>
    /// <param name="FileCount">Sayılan dosya. <paramref name="Measured"/> <c>false</c> ise gerçek
    /// sayı EN AZ bu kadardır.</param>
    /// <param name="Measured">Ağacın tamamı görülebildi mi.</param>
    /// <param name="TruncatedReasonKey">Kesilme sebebinin çeviri anahtarı; ölçüm tamsa boş.</param>
    internal readonly record struct FileCountMeasurement(int FileCount, bool Measured, string TruncatedReasonKey);

    /// <summary>
    /// <b>ÖLÇÜLDÜ (denetim Y7):</b> içinde kavşak bulunan gerçek bir site ağacında
    /// <c>Directory.Delete(recursive: true)</c> istisna attı, <b>ama <c>index.php</c>,
    /// <c>uploads\photo.jpg</c> ve <c>wwwroot\a.php</c> silinmişti</b>; yalnız boş <c>wwwroot</c>
    /// kaldı. Panel bunu "Site dosyaları silinemedi" diye raporluyordu — yani gerçeğin TERSİ.
    /// Kullanıcı "silinemedi" okuyup dosyalarının durduğunu sanıyordu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÜÇÜNCÜ DURUM (bu turda eklendi):</b> karar iki sayımın FARKINA bakar, ama sayımlar
    /// bütçelidir. İkisi de öge tavanına dayanırsa fark SIFIR çıkar; eski kural bunu
    /// <see cref="DirectoryDeletionOutcome.NotDeleted"/> sayıp "silinemedi, dosyalar duruyor"
    /// diyordu — ağacın çoğu gitmiş olabilecekken KESİN bir cümle. Tavanı 20.000'den 200.000'e
    /// çıkarmak (ölçüldü: panelin kendi <c>node_modules</c>'ü tek başına 17.459 dosya) yalnızca
    /// olasılığı düşürdü, sınıfı kapatmadı. Artık kapatan şey şu: ölçülemeyen hâl, ölçülen iki
    /// hâlden birine YUVARLANMIYOR.
    /// </para>
    /// <para>
    /// <b>Kesik ölçümle de KANITLANABİLEN tek hâl korunuyor</b> (aksi hâlde üçüncü durum, gerçekten
    /// bilinen şeyleri de "bilmiyorum"a çevirirdi): önceki sayım kesikse <c>önceki_gerçek ≥ önceki</c>
    /// olur; kalan sayım TAMSA <c>kalan</c> kesindir. <c>kalan &lt; önceki</c> ise
    /// <c>kalan &lt; önceki ≤ önceki_gerçek</c>, yani gerçek fark KESİN pozitiftir → kısmi silme
    /// kanıtlanmıştır. Bu, tavanı aşan büyük ağaçlarda en sık karşılaşılan durumdur (silme neredeyse
    /// her şeyi götürür, geriye kilitli birkaç dosya kalır) ve orada "bilmiyorum" demek gereksiz
    /// bir bilgi kaybı olurdu.
    /// </para>
    /// <para>
    /// <b>Ters yön (önceki TAM, kalan KESİK) kanıt DEĞİLDİR:</b> kalan yalnız bir alt sınırdır,
    /// gerçek kalan öncekine eşit olabilir — yani "kısmen silindi" demek yine yalan olabilirdi.
    /// O hâl bilerek üçüncü duruma düşer.
    /// </para>
    /// </remarks>
    internal static DirectoryDeletionOutcome ClassifyDeletion(
        FileCountMeasurement oncesi,
        FileCountMeasurement kalan,
        bool klasorDuruyor)
    {
        // Klasörün YOKLUĞU ölçüm değil GÖZLEMDİR: bütçenin bununla ilgisi yok.
        if (!klasorDuruyor)
        {
            return DirectoryDeletionOutcome.Deleted;
        }

        if (oncesi.Measured && kalan.Measured)
        {
            return kalan.FileCount < oncesi.FileCount
                ? DirectoryDeletionOutcome.PartiallyDeleted
                : DirectoryDeletionOutcome.NotDeleted;
        }

        if (kalan.Measured && kalan.FileCount < oncesi.FileCount)
        {
            return DirectoryDeletionOutcome.PartiallyDeleted;
        }

        return DirectoryDeletionOutcome.MeasurementIncomplete;
    }

    /// <summary>Kullanıcıya ne yazılacağı — saf işlev, mesajın kendisi de test edilebilir olsun diye.</summary>
    /// <remarks>
    /// <para>
    /// <b>ÜÇÜNCÜ DURUMDA NE SÖYLENİYOR ve NEDEN:</b> bu ekran bir silme sonucudur; kullanıcının tek
    /// sorusu "verim gitti mi, kaldı mı". Cevap bilinmediğinde iki seçenek vardı ve ikisi de
    /// tartıldı: (a) "silindi" ya da "silinemedi"den birini seçmek — ikisi de kullanıcıyı YANLIŞ bir
    /// işe gönderir ("silindi" derse gitmeyen dosyalar sunucuda ve kotasında kalır; "silinemedi"
    /// derse geri getirilemeyecek bir kaybı fark etmeden bekler). (b) Bilmediğimizi SÖYLEYİP ne
    /// yapması gerektiğini yazmak. Bu depoda kural (b): rahatsız edici ama yanlış değil. Cümle bu
    /// yüzden üç şeyi birden taşır — klasörün DURDUĞU (gözlem), içeriğinin akıbetinin
    /// ÖLÇÜLEMEDİĞİ, ve kullanıcının klasörü ELLE açıp bakması gerektiği.
    /// </para>
    /// <para>
    /// <b>Sayılar neden ">=" ile basılıyor:</b> kesik bir sayım alt sınırdır. Çıplak basmak,
    /// kaldırdığımız yalanı bir satır aşağıda tekrarlamak olurdu.
    /// </para>
    /// <para>
    /// <b>Teknik kuyruk neden ETİKET-DEĞER:</b> kesilme sebebi bir ÇEVİRİ ANAHTARIDIR
    /// (<c>files.size_truncated_*</c>) ve bu liste arayüzde olduğu gibi basılır (<c>t()</c>'den
    /// geçmez). Anahtarı Türkçe anlatının içine gömmek kullanıcıya ham anahtar gösterirdi; aynı
    /// karar <c>BackupService</c>'te ölçülerek verildi (orada <c>Detail</c> alanı
    /// "scan=truncated; reason=..." biçimindedir). Anlatı Türkçedir çünkü bu listenin TAMAMI
    /// (uyarılar) sunucuda Türkçe kuruluyor; teşhis satırı ise dilden bağımsızdır.
    /// </para>
    /// </remarks>
    internal static string DescribeDeletionFailure(
        string label,
        string path,
        DirectoryDeletionOutcome outcome,
        FileCountMeasurement oncesi,
        FileCountMeasurement kalan,
        string reason)
    {
        return outcome switch
        {
            DirectoryDeletionOutcome.Deleted => $"{label}: silindi ({path}) — {reason}",
            DirectoryDeletionOutcome.PartiallyDeleted =>
                $"{label}: KISMEN SİLİNDİ — {Sayi(oncesi)} dosyadan {SilinenSayi(oncesi, kalan)} tanesi " +
                $"silindi, {Sayi(kalan)} tanesi kaldı ({path}). Silinenler GERİ GETİRİLEMEZ; " +
                $"kalanları elle temizlemeniz gerekir. Sebep: {reason}",
            DirectoryDeletionOutcome.MeasurementIncomplete =>
                $"{label}: SONUÇ ÖLÇÜLEMEDİ — klasör sunucuda duruyor ({path}) ama içindeki dosyaların " +
                $"ne kadarının silindiği SAYILAMADI: dosyalar gitmiş de olabilir, duruyor da olabilir. " +
                $"Gitmiş olanlar GERİ GETİRİLEMEZ. Klasörü elle açıp içeriğini kontrol edin. " +
                $"Sebep: {reason} [measurement=truncated; before={Sayi(oncesi)}; after={Sayi(kalan)}; " +
                $"reason_key={KesilmeSebebi(oncesi, kalan)}]",
            _ => $"{label}: silinemedi, dosyalar duruyor ({path}) — {reason}"
        };
    }

    /// <summary>Kesik ölçüm ALT SINIR olarak basılır; çıplak sayı, olmayan bir kesinlik iddia ederdi.</summary>
    private static string Sayi(FileCountMeasurement olcum)
        => olcum.Measured ? olcum.FileCount.ToString() : $">={olcum.FileCount}";

    /// <summary>
    /// "Kaç tanesi silindi" — ve o da bir ALT SINIRDIR ön sayım kesikse. Farkı çıplak basmak,
    /// bir satır yukarıda kaldırdığımız kesinlik iddiasını aynı cümlenin ortasında tekrarlamak olurdu.
    /// </summary>
    private static string SilinenSayi(FileCountMeasurement oncesi, FileCountMeasurement kalan)
    {
        var fark = oncesi.FileCount - kalan.FileCount;
        return oncesi.Measured ? fark.ToString() : $">={fark}";
    }

    /// <summary>
    /// Raporlanacak kesilme sebebi. ÖNCEKİ ölçümün sebebi önceliklidir: kararı belirsiz bırakan
    /// asıl ölçüm odur (kalan ölçüm tamken de üçüncü duruma düşülebilir).
    /// </summary>
    private static string KesilmeSebebi(FileCountMeasurement oncesi, FileCountMeasurement kalan)
        => !string.IsNullOrEmpty(oncesi.TruncatedReasonKey)
            ? oncesi.TruncatedReasonKey
            : kalan.TruncatedReasonKey;

    /// <summary>
    /// Bu sayımın öge bütçesi. <b>ESKİ DEĞER 20.000'Dİ VE ÖLÇÜLEREK YÜKSELTİLDİ.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kesilen sayım burada ne KIRAR:</b> bu sayı kullanıcıya silmeden ÖNCE gösterilmez —
    /// çağrı grafiği ölçüldü, ön sayımın tek tüketicisi <see cref="ClassifyDeletion"/> ve
    /// <see cref="DescribeDeletionFailure"/>, ikisi de <c>Directory.Delete</c> ZATEN çalıştıktan
    /// sonraki istisna dalında. Yani "3 dosya silinecek" yazıp 300.000 dosya sildiren bir onay
    /// ekranı bu yolda YOKTUR. Kırılan şey TERSİ: iki ölçüm de tavana dayandığında farkları 0 olur,
    /// karar <see cref="DirectoryDeletionOutcome.NotDeleted"/>'a düşer ve panel "silinemedi,
    /// dosyalar duruyor" der — oysa ağacın çoğu gitmiş olabilir. Bu, Y7 denetiminin kapattığı
    /// YALANIN ta kendisi, sadece tavanın üstüne taşınmış hâli.
    /// </para>
    /// <para>
    /// <b>20.000 neden yetmiyordu — BU MAKİNEDE ÖLÇÜLDÜ:</b> panelin KENDİ
    /// <c>frontend/node_modules</c> ağacı tek başına <b>17.459 dosya</b>. Yani tek bir bağımlılık
    /// klasörü eski tavanın %87'sini yiyordu; site içeriği eklenince tavan aşılıyordu. Yalan, uç
    /// bir senaryo değil olağan bir Node/PHP sitesinde çıkardı. Ayrıca ortak yürüyüşün bütçesi
    /// DOSYA değil ÖGE (dosya + klasör) sayar; 20.000'i olduğu gibi taşımak sessiz bir DARALTMA
    /// olurdu.
    /// </para>
    /// <para>
    /// <b>Yükseltmenin bedeli ölçüldü ve yok denecek kadar az:</b> sayım en çok iki kez, hemen
    /// ardından <c>Directory.Delete(recursive: true)</c> ile ZATEN baştan sona gezilecek bir ağaçta
    /// koşar — yürüyüş, peşinden gelen silmeden ucuzdur. Yani bu yolu hızlı tutan şey tavan değil.
    /// 200.000 değeri uydurulmadı: panelin kullanıcıya cevap veren yollarının hepsinde aynı sayı
    /// var (<c>FileService.BoyutOgeButcesi</c>, <c>FileService.KopyaOgeButcesi</c>,
    /// <c>CustomerScopeService.KotaOgeButcesi</c>, <c>SiteMigrationService.UserIniOgeButcesi</c>).
    /// </para>
    /// <para>
    /// <b>SÜRE BÜTÇESİ BİLEREK YOK</b> (<c>int.MaxValue</c>, aynı karar <c>FileService</c>
    /// kopyalamasında ve içe aktarmada da verildi): buradaki karar iki ÖLÇÜMÜN KARŞILAŞTIRMASIDIR.
    /// Süre tavanı belirlenimci değildir — aynı ağaç diskin o anki hâline göre iki farklı sayı
    /// verebilir ve tek başına bu, <c>NotDeleted</c>'ı <c>PartiallyDeleted</c>'a (ya da tersine)
    /// çevirirdi. Öge tavanı belirlenimcidir: aynı ağaç → aynı sayı.
    /// </para>
    /// <para>
    /// <b>O AÇIK ARTIK KAPALI:</b> 200.000'in üstünde de düşme YOK — sayım kesildiğinde
    /// <see cref="ClassifyDeletion"/> <see cref="DirectoryDeletionOutcome.MeasurementIncomplete"/>
    /// döndürüyor ve kullanıcıya kesin bir cümle KURULMUYOR. Yani tavan artık bir DOĞRULUK sınırı
    /// değil, yalnızca bir maliyet sınırı: aşıldığında panel yanlış konuşmuyor, "ölçemedim" diyor.
    /// </para>
    /// </remarks>
    private const int SayimOgeButcesi = 200_000;

    /// <summary>
    /// Klasördeki dosya sayısı. <b>Kavşak/sembolik bağlantı klasörlere GİRMEZ</b> — özyinelemeli
    /// sayımın kavşağı takip ettiği bu depoda ölçüldü (denetim Y6) ve sayım da bir okuma yoludur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Yürüyüş ARTIK KENDİ DEĞİL:</b> gövde <see cref="SafeDirectoryWalk"/>'a indirildi. Buradaki
    /// elle yazılmış kuyruk yürüyüşü panelin BEŞİNCİ kopyasıydı ve korumaları (kavşak atlaması +
    /// tavan) doğruydu; yine de kaldırıldı, çünkü <c>SafeDirectoryWalk</c>'un varlık sebebi
    /// "yürüyüş TEK YERDE" iddiasıdır ve o iddia beşinci kopyayla YANLIŞ olurdu. Bu turda tam olarak
    /// öyle bir kopya (<c>FileService.KlasoruKopyala</c>) nöbetçinin BAKMADIĞI bir dosyada durduğu
    /// için gözden kaçtı ve gerçek bir açık üretti.
    /// </para>
    /// <para>
    /// <b>KÖKÜN KENDİSİ kavşaksa</b> eski gövde 0 dönerdi, ortak yürüyüş ise bağlantının ardını
    /// sayar. Kullanıcının GÖRDÜĞÜ hiçbir şey değişmez ve bu ölçüldü: kök bir kavşaksa iki ölçüm de
    /// aynı sayıyı verir, fark 0 kalır, karar yine <see cref="DirectoryDeletionOutcome.NotDeleted"/>
    /// olur (silme başarılıysa zaten klasör yok → <c>Deleted</c>). Sayılar yalnız
    /// <c>PartiallyDeleted</c> mesajında basılır ve o dal bu durumda hiç oluşmaz. Kök denetimini
    /// burada yeniden yazmak, nöbetçinin yasakladığı <c>ReparsePoint</c> denetimini
    /// gövdeye geri koymak — yani korumayı ikinci bir yerde tekrar etmek — olurdu.
    /// </para>
    /// <para>
    /// Okunamayan alt klasör sayıma girmez (ortak yürüyüş erişim reddini ve G/Ç hatasını yutup
    /// "kısmi" damgalar); karar iki ölçümün FARKINA bakar, mutlak sayıya değil.
    /// </para>
    /// <para>
    /// <b>DÖNÜŞ TİPİ DÜZ SAYI DEĞİL</b> ve sebebi ölçüldü: yürüyüş "bu sayı tam mı" bilgisini
    /// (<see cref="SafeDirectoryWalkResult.Truncated"/> + sebep anahtarı) ZATEN üretiyordu, ama
    /// <c>int</c> dönüşü onu burada düşürüyordu. Bilgi kaynakta varken yolda kaybolduğu için
    /// <see cref="ClassifyDeletion"/> iki alt sınırı kesin ölçümmüş gibi karşılaştırıyor ve
    /// kullanıcıya ölçülmemiş bir şeyi KESİN cümleyle söylüyordu. Aynı düşme disk kotasında da
    /// yaşandı; oradaki çözüm (<c>DiskUsage</c>) burada birebir tekrarlandı.
    /// </para>
    /// </remarks>
    /// <param name="ogeButcesi">Yalnız TEST içindir; üretim çağrılarının hiçbiri vermez (tavan
    /// davranışı gerçek bir ağaçla ölçülebilsin diye).</param>
    internal static FileCountMeasurement CountFilesSafely(string root, int ogeButcesi = SayimOgeButcesi)
    {
        // İPTAL JETONU YOK (CancellationToken.None): ölçüm, silme kararının kendisini kuruyor.
        // İptal edilebilseydi yarıda kesilen bir sayım "hiç dosya yoktu" gibi görünür ve panel
        // gitmiş dosyaları hiç saymadan "silindi" derdi.
        var olcum = SafeDirectoryWalk.Yuru(
            root,
            new SafeDirectoryWalkBudget(ogeButcesi, int.MaxValue),
            CancellationToken.None);

        return new FileCountMeasurement(olcum.FileCount, !olcum.Truncated, olcum.TruncatedReasonKey);
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
        bool deleteDatabases,
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

                // Kalıntı YALNIZCA "veritabanlarını silme" seçildiğinde buradan bildirilir.
                // Silme İSTENDİĞİ hâlde hayatta kalan bir veritabanı zaten DROP adımında
                // başarısız olmuştur ve orada kalıntı olarak yazılmıştır — ikinci kez yazmak
                // aynı veritabanını listede iki kere gösterirdi.
                if (!deleteDatabases)
                {
                    Kalinti(
                        result,
                        SiteLeftoverKinds.Database,
                        $"{engine}:{databaseName}",
                        "veritabanlarını silme seçilmedi — motorda duruyor, sahipliği korundu",
                        deliberate: true);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Veritabani sahipligi korunamadi: {Engine}:{Name}", engine, databaseName);
                result.Warnings.Add(
                    $"Veritabanı sahiplik kaydı ({engine}:{databaseName}): güncellenemedi — {exception.Message}");

                // Kayıt güncellenemedi: veritabanı motorda, panelde ise ÖLÜ bir site adına bağlı
                // görünmeye devam ediyor. Sessiz geçilirse kimse fark etmez.
                Kalinti(
                    result,
                    SiteLeftoverKinds.Database,
                    $"{engine}:{databaseName}",
                    $"sahiplik kaydı güncellenemedi, silinmiş '{siteName}' sitesine bağlı görünüyor — {exception.Message}");
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
