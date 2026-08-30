using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Waf;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Iis;

public sealed class SiteProvisioningService(
    IIisSiteService iisSiteService,
    IComponentBootstrapper componentBootstrapper,
    IPhpService phpService,
    IAspNetService aspNetService,
    IAppRuntimeService appRuntimeService,
    ISiteDefaultsService siteDefaultsService,
    IWafService wafService,
    ILogger<SiteProvisioningService> logger) : ISiteProvisioningService
{
    public async Task<SiteDto> CreateAndProvisionAsync(
        CreateSiteRequest request,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var physicalPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.PhysicalPath.Trim()));
        var directoryExisted = Directory.Exists(physicalPath);
        SiteDto? site = null;

        try
        {
            site = await iisSiteService.CreateSiteAsync(request, cancellationToken);
            var profile = ResolveProfile(request.RuntimeProfile);
            await ApplyProfileAsync(site, profile, request, actorUserId, ipAddress, cancellationToken);
            site = await iisSiteService.GetSiteAsync(site.Name, cancellationToken);
            _ = await siteDefaultsService.EnsureDefaultsAsync(site, cancellationToken);
            await GrantSiteWritableAsync(site, cancellationToken);
            return site;
        }
        catch
        {
            if (site is not null)
            {
                try
                {
                    await iisSiteService.DeleteSiteAsync(site.Name, CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    logger.LogError(rollbackException, "Unable to roll back site {SiteName}.", site.Name);
                }
            }

            if (!directoryExisted && Directory.Exists(physicalPath))
            {
                try
                {
                    Directory.Delete(physicalPath, recursive: true);
                }
                catch (Exception cleanupException)
                {
                    logger.LogWarning(cleanupException, "Unable to remove site directory {PhysicalPath} after rollback.", physicalPath);
                }
            }

            throw;
        }
    }

    // #5 — Uygulama (PHP FastCGI) site kökünde KENDİ app pool kimliğiyle çalışır; kendi köküne YAZABİLSİN:
    // Laravel storage/ + bootstrap/cache/, WordPress wp-content/uploads vb. (aksi halde "Permission denied"/500).
    // YALNIZ bu sitenin app pool'una verilir → başka site bu köke yazamaz (kiracı izolasyonu). (OI)(CI) → sonradan
    // eklenen/göç edilen dosyalar da miras alır. Best-effort: izin verilemese de site kurulumu başarısız sayılmaz.
    //
    // HAVUZ ADI ARTIK YALNIZ site.ApplicationPoolName'DEN OKUNUR -- PARAMETRE KALDIRILDI.
    //
    // OLCULMUS ARIZA: metot eskiden bir `applicationPoolName` parametresi aliyordu ve bos gelirse
    // `site.Name`'e dusuyordu. Olusturma yolu (CreateSiteAsync) ona `request.ApplicationPoolName`
    // veriyordu; o alan panelden gelen HER istekte BOS (SitesController musteri/bayi icin bilerek
    // string.Empty yapiyor, arayuz de her create'te "" gonderiyor). Yani havuz adi HER ZAMAN
    // `site.Name`'e dusuyordu -- ornegin "musteri.com".
    //
    // IIS'te o havuz BU ADLA YOK: havuzu WebAdministrationSiteService.BuildManagedPoolName kuruyor
    // ve adi "AdaPanel." onekli oluyor ("AdaPanel.musteri.com"). icacls olmayan bir hesap icin
    // "No mapping between account names and security IDs was done" (hata 1332) dondurur ve
    // HICBIR SEY UYGULAMAZ. Sonuc: panelden acilan HICBIR sitede olusturma anindaki Modify izni
    // gercekten verilmiyordu. Basarisizlik yalnizca LogWarning'e yaziliyordu, kullanici ise
    // Laravel storage/ ya da WordPress uploads yazamadigi icin 500 goruyordu.
    //
    // BELIRTIYI MASKELEYEN SEY: "Izinleri Onar" (RepairPermissionsAsync) AYNI metodu
    // `site.ApplicationPoolName` ile cagiriyordu -- yani DOGRU adla. Ozellik "onarinca duzeliyor,
    // olustururken hic calismiyor" gorunuyordu; bu, kok nedeni aramayi yillarca yanlis yere
    // yonlendirebilecek bir isaretti.
    //
    // Neden parametre tamamen KALDIRILDI: `site` her iki cagirma yolunda da IIS'ten TAZE okunuyor
    // (CreateSiteAsync:39, RepairPermissionsAsync:227), dolayisiyla site.ApplicationPoolName zaten
    // dis sistemin GERCEK degeridir -- yonetici baska bir havuzu yeniden kullandiginda da dogrudur.
    // Parametreyi birakmak, ayni yanlis degeri gelecekte tekrar gecirebilmek demekti; tahmin edilen
    // ad yerine OLCULEN adi kullanmak bu deponun kurali.
    private async Task GrantSiteWritableAsync(SiteDto site, CancellationToken cancellationToken)
    {
        try
        {
            if (!OperatingSystem.IsWindows() ||
                string.IsNullOrWhiteSpace(site.PhysicalPath) ||
                !Directory.Exists(site.PhysicalPath))
            {
                return;
            }

            var pool = site.ApplicationPoolName;

            // BOSSA site.Name'e DUSMUYORUZ. Eski davranis tam olarak buydu ve var olmayan bir
            // hesaba izin vermeye calisiyordu: icacls 1332 ile hicbir sey yapmiyor, biz de
            // "izin verildi" satirini yazmasak bile GERCEK sebebi gizlemis oluyorduk. Havuz adini
            // OKUYAMADIYSAK yapilacak dogru is, uydurmak degil, bunu SOYLEMEKTIR.
            if (string.IsNullOrWhiteSpace(pool))
            {
                logger.LogWarning(
                    "Site yazma izni ATLANDI: {Site} icin uygulama havuzu adi IIS'ten OKUNAMADI. " +
                    "Havuz adi tahmin edilmez; izin uygulanmadi.",
                    site.Name);
                return;
            }

            var result = await ProcessRunner.RunAsync(
                "icacls.exe",
                $"\"{site.PhysicalPath}\" /grant \"IIS AppPool\\{pool}:(OI)(CI)M\" /T /C /Q",
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(60));

            // ProcessRunner EXCEPTION FIRLATMAZ, CommandResult döndürür. icacls'in en tipik hatası
            // ("No mapping between account names and security IDs was done" — app pool yoksa ya da adı
            // farklıysa) sıfırdan farklı çıkış kodudur, exception değil. Sonuç okunmadığı için
            // aşağıdaki catch bu vakada HİÇ tetiklenmiyor, hemen ardından "izin verildi" yazılıyordu:
            // kullanıcı "İzinleri Onar"a basıp başarı görüyor, izinler hiç değişmiyor ve 500 hatasının
            // sebebini başka yerde arıyordu.
            if (result.Success)
            {
                logger.LogInformation("Site yazma izni verildi (app pool kimliği): {Site} -> {Path}", site.Name, site.PhysicalPath);
            }
            else
            {
                logger.LogWarning(
                    "Site yazma izni VERİLEMEDİ: {Site} -> {Path} (havuz: {Pool}). icacls çıkışı: {Error}",
                    site.Name,
                    site.PhysicalPath,
                    pool,
                    string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Site yazma izni verilemedi (en iyi çaba): {Site}", site.Name);
        }

        await IsolateSiteAsync(site, cancellationToken);
    }

    /// <summary>
    /// Site kökünü diğer kiracılardan YALITIR. Yeni sitede ve "İzinleri Onar" akışında çalışır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden gerekliydi:</b> site kökü yalnızca kendi havuz kimliğine yazma hakkı alıyordu ama
    /// üst klasörden gelen ACL <b>mirası hiç kırılmıyordu</b>. Miras kırılmadığında üstten gelen
    /// <c>IIS_IUSRS</c>/<c>Users</c> okuma hakkı TÜM havuz kimliklerine geçer (havuz sanal
    /// hesapları IIS_IUSRS üyesidir) — yani A sitesinin PHP'si B sitesinin <c>wp-config.php</c>
    /// dosyasını, dolayısıyla veritabanı parolasını okuyabiliyordu.
    /// </para>
    /// <para>
    /// <b>Uygulama yeni değil:</b> izolasyonun kendisi (<c>WafService.ApplyFolderAclAsync</c>)
    /// zaten vardı ve doğruydu — anonim kimliği IUSR yerine havuz kimliğine çeviriyor (yoksa
    /// statik dosyalar 401 verirdi), SYSTEM/Administrators haklarını SID ile (locale bağımsız)
    /// bırakıyor ve anonim kimlik ayarlanamazsa icacls'e hiç geçmiyor. Eksik olan tek şey,
    /// bunun <b>varsayılan olarak uygulanmamasıydı</b>: her site için elle basılması gerekiyordu,
    /// yani yeni açılan her site yalıtımsız kalıyordu.
    /// </para>
    /// <para>
    /// <b>En iyi çaba:</b> yalıtım uygulanamazsa site oluşturma BAŞARISIZ SAYILMAZ. Aksi hâlde
    /// tek bir ACL sorunu kullanıcının site açmasını tümden engellerdi; bunun yerine uyarı
    /// günlüğe yazılır ve kullanıcı "İzinleri Onar" ile tekrar deneyebilir.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Panelin kendi altyapı sitelerinin ad öneki (webmail, phpMyAdmin, Adminer).
    /// Önek İKİ YERDE TANIMLANMAZ: sahiplik işaretinin tek kaynağı <see cref="ManagedAppPoolName.Prefix"/>.
    /// </summary>
    private const string ManagedSitePrefix = ManagedAppPoolName.Prefix;

    private async Task IsolateSiteAsync(SiteDto site, CancellationToken cancellationToken)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(site.PhysicalPath))
            {
                return;
            }

            // PANELİN KENDİ ARAÇ SİTELERİ HARİÇ (webmail, phpMyAdmin, Adminer — "AdaPanel." önekli).
            //
            // Bunlar kiracı değil, panelin parçası; onları diğer kiracılardan yalıtmanın güvenlik
            // kazancı YOK. Buna karşılık risk gerçek ve ölçülmüş: bu depoda araç sitelerinin ACL'ini
            // sıkılaştırmak üç ayrı sürümde (0.2.91, 0.2.92, 0.2.93) phpMyAdmin/Adminer'i kırdı ve
            // her seferinde kullanıcı hatayı canlıda buldu. Kazancı olmayan bir değişiklik için o
            // riski almıyoruz.
            if (site.Name.StartsWith(ManagedSitePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Principal boş: WafService siteyi taze okuyup O ANKİ havuz kimliğini kullanır.
            await wafService.ApplyFolderAclAsync(
                site.Name,
                new ApplyFolderAclRequest { Principal = string.Empty, Rights = string.Empty },
                cancellationToken);

            logger.LogInformation("Site kiracı izolasyonu uygulandı: {Site} -> {Path}", site.Name, site.PhysicalPath);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Site kiracı izolasyonu uygulanamadı (en iyi çaba, site yine de kuruldu): {Site}",
                site.Name);
        }

        // İKİNCİ KATMAN — PHP izolasyonu (open_basedir), NTFS'ten AYRI bir try: biri başarısız olursa
        // öteki yine uygulanır.
        //
        // NEDEN BURADA, PROFİLİN İÇİNDE DEĞİL — ÖLÇÜLMÜŞ AÇIK: open_basedir yalnız
        // PhpService.SetSitePhpVersionAsync yolunda yazılıyordu; onu ise SADECE "php" profili çağırır.
        // Panelin VARSAYILAN profili "auto"dur (CreateSiteRequest.RuntimeProfile = "auto") ve o yol
        // AspNetService.EnableMixedHandlersAsync üzerinden gider — yani PANELDEN AÇILAN VARSAYILAN
        // SİTENİN PHP'Sİ İZOLASYONSUZ kalıyordu. Bu makinede php-cgi ile ölçüldü: open_basedir boş,
        // komşu sitenin dosyası okundu. Çağrı artık profilden BAĞIMSIZ tek yerde: siteyi izole etme
        // kararı "hangi runtime seçildi" sorusuna bağlı değildir.
        try
        {
            await phpService.EnsureSiteIsolationAsync(site.Name, enabled: true, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Site PHP izolasyonu (open_basedir) uygulanamadı (en iyi çaba, site yine de kuruldu): {Site}",
                site.Name);
        }
    }

    // #1 ⭐ Belge kökü / web root: IIS fiziksel yolunu değiştir (Laravel/Symfony için ...\public) ve KALICI yap.
    // Yeni kökte index.php varsayılan belge + temel web.config yazılır — IIS'in stok varsayılan-belge listesi
    // index.php İÇERMEZ, bu yüzden docroot=public olunca "/" 403 verirdi. Mevcut web.config'i (handler/rewrite)
    // EZMEZ; eksik öğeleri ekler (idempotent merge).
    public async Task<SiteDto> SetDocumentRootAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.UpdatePhysicalPathAsync(siteName, physicalPath, cancellationToken);

        try
        {
            WebAppPublisher.EnsurePhpEntry(site.PhysicalPath);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Belge kökü web.config'i yazılamadı (en iyi çaba): {Site}", siteName);
        }

        return site;
    }

    // #2 — Laravel/WordPress/Symfony pretty-URL yönlendirmesi (index.php front controller). URL Rewrite MODÜLÜ
    // gerekir; yoksa <rewrite> tüm siteyi 500.19 yapar → modül yoksa hiç yazılmaz, net hata verilir (site bozulmaz).
    public async Task<SiteDto> EnableFrameworkRewriteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        if (!WebAppPublisher.TryWriteFrontControllerRewrite(site.PhysicalPath))
        {
            throw new AppException("sites.url_rewrite_module_missing", 409, 409);
        }

        return site;
    }

    // #5 onar — mevcut siteye yazma iznini (app pool kimliği, kök Modify) YENİDEN uygula. Bu özellikten önce
    // kurulmuş siteler için (oluşturma anında otomatik verilen izin onlarda yoktu). Best-effort.
    //
    // MEVCUT SİTELER İÇİN VERİLEN KARAR (PHP izolasyonu / open_basedir):
    // Güncelleme, çalışan bir siteyi KENDİLİĞİNDEN kısıtlamaz. Panel yükseltildiğinde var olan
    // sitelerin php.ini'sine hiçbir [PATH=] bloğu yazılmaz — kökünün dışındaki bir klasörü okuyan
    // (paylaşılan kütüphane, eski göç düzeni) bir site sessizce 500 vermeye başlamamalıdır.
    // İzolasyon var olan bir siteye ÜÇ açık eylemden biriyle gelir:
    //   1) bu "İzinleri Onar" düğmesi,
    //   2) siteye PHP sürümü atamak (PhpService.SetSitePhpVersionAsync),
    //   3) PHP ayarları ekranındaki izolasyon anahtarı.
    // YENİ sitelerde ise varsayılan AÇIK'tır (CreateAndProvisionAsync → IsolateSiteAsync).
    // Böylece yükseltme geri alınabilir kalır: bir sitede sorun çıkarsa yönetici anahtarı kapatır.
    public async Task<SiteDto> RepairPermissionsAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        await GrantSiteWritableAsync(site, cancellationToken);
        return site;
    }

    private static string ResolveProfile(string requestedProfile)
    {
        var profile = requestedProfile.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(profile) ? "auto" : profile;
    }

    private async Task ApplyProfileAsync(
        SiteDto site,
        string profile,
        CreateSiteRequest request,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        switch (profile)
        {
            case "auto":
                try
                {
                    var componentId = await EnsurePhpAsync(request, actorUserId, ipAddress, cancellationToken);
                    WriteMixedStarter(site.PhysicalPath);
                    await aspNetService.EnableMixedHandlersAsync(
                        site.Name,
                        new EnableMixedHandlersRequest
                        {
                            PhpComponentId = componentId,
                            EnableClassicAsp = true,
                            ManagedRuntimeVersion = "v4.0",
                            ManagedPipelineMode = "Integrated"
                        },
                        cancellationToken);
                }
                catch (AppException exception) when (
                    exception.MessageKey is
                        "php.version_not_installed" or
                        "php.invalid_component" or
                        "software.download_not_found" or
                        "software.download_failed" or
                        "software.install_failed" or
                        "software.install_validation_failed" or
                        "software.process_failed" or
                        "software.process_start_failed" or
                        "software.checksum_mismatch" or
                        "software.checksum_required" or
                        "software.signature_invalid" or
                        "software.executable_not_found" or
                        "software.dependency_not_found" or
                        "software.dependency_cycle")
                {
                    logger.LogWarning(
                        exception,
                        "Automatic runtime profile fell back to ASP.NET for site {SiteName}.",
                        site.Name);
                    WriteAspNetStarter(site.PhysicalPath);
                    await aspNetService.SetDotNetVersionAsync(
                        site.Name,
                        new UpdateDotNetRuntimeRequest
                        {
                            ManagedRuntimeVersion = "v4.0",
                            ManagedPipelineMode = "Integrated"
                        },
                        cancellationToken);
                }

                return;

            case "static":
                return;
            case "php":
            {
                var componentId = await EnsurePhpAsync(request, actorUserId, ipAddress, cancellationToken);
                WritePhpStarter(site.PhysicalPath);
                await phpService.SetSitePhpVersionAsync(
                    site.Name,
                    new SetSitePhpVersionRequest { ComponentId = componentId },
                    cancellationToken);
                return;
            }
            case "aspnet":
                WriteAspNetStarter(site.PhysicalPath);
                await aspNetService.SetDotNetVersionAsync(
                    site.Name,
                    new UpdateDotNetRuntimeRequest
                    {
                        ManagedRuntimeVersion = "v4.0",
                        ManagedPipelineMode = "Integrated"
                    },
                    cancellationToken);
                return;
            case "classic-asp":
                WriteClassicAspStarter(site.PhysicalPath);
                await aspNetService.SetClassicAspAsync(
                    site.Name,
                    new UpdateClassicAspRequest { Enabled = true },
                    cancellationToken);
                return;
            case "mixed":
            {
                var componentId = await EnsurePhpAsync(request, actorUserId, ipAddress, cancellationToken);
                WriteMixedStarter(site.PhysicalPath);
                await aspNetService.EnableMixedHandlersAsync(
                    site.Name,
                    new EnableMixedHandlersRequest
                    {
                        PhpComponentId = componentId,
                        EnableClassicAsp = true,
                        ManagedRuntimeVersion = "v4.0",
                        ManagedPipelineMode = "Integrated"
                    },
                    cancellationToken);
                return;
            }
            case "node":
            {
                const string componentId = "nodejs-lts";
                await EnsureComponentAsync(componentId, request.InstallRuntimeIfMissing, actorUserId, ipAddress, cancellationToken);
                await EnsureComponentAsync("http-platform-handler", request.InstallRuntimeIfMissing, actorUserId, ipAddress, cancellationToken);
                var startupFile = string.IsNullOrWhiteSpace(request.StartupFile) ? "server.js" : request.StartupFile.Trim();
                WriteNodeStarter(site.PhysicalPath, startupFile);
                await appRuntimeService.HostNodeAppAsync(
                    site.Name,
                    new HostNodeAppRequest
                    {
                        ComponentId = componentId,
                        StartupFile = startupFile,
                        WorkingDirectory = ".",
                        EnvironmentVariables = ["NODE_ENV=production"]
                    },
                    cancellationToken);
                return;
            }
            case "python":
            {
                const string componentId = "python-3";
                await EnsureComponentAsync(componentId, request.InstallRuntimeIfMissing, actorUserId, ipAddress, cancellationToken);
                await EnsureComponentAsync("http-platform-handler", request.InstallRuntimeIfMissing, actorUserId, ipAddress, cancellationToken);
                var startupFile = string.IsNullOrWhiteSpace(request.StartupFile) ? "app.py" : request.StartupFile.Trim();
                WritePythonStarter(site.PhysicalPath, startupFile);
                await appRuntimeService.HostPythonAppAsync(
                    site.Name,
                    new HostPythonAppRequest
                    {
                        ComponentId = componentId,
                        StartupFile = startupFile,
                        WorkingDirectory = ".",
                        EnvironmentVariables = ["PYTHONUNBUFFERED=1"]
                    },
                    cancellationToken);
                return;
            }
            default:
                throw new AppException("sites.invalid_runtime_profile", 400, 400);
        }
    }

    private async Task<string> EnsurePhpAsync(
        CreateSiteRequest request,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var requestedId = request.RuntimeComponentId.Trim();
        var componentId = !string.IsNullOrWhiteSpace(requestedId)
            ? requestedId
            : catalog.Installed
                .Where(component =>
                    component.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(component.Status, "Installed", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(component => component.Version, StringComparer.OrdinalIgnoreCase)
                .Select(component => component.ComponentId)
                .FirstOrDefault()
              ?? catalog.Manifest
                  .Where(component => component.Id.StartsWith("php-", StringComparison.OrdinalIgnoreCase))
                  .OrderByDescending(component => component.Version, StringComparer.OrdinalIgnoreCase)
                  .Select(component => component.Id)
                  .FirstOrDefault()
              ?? throw new AppException("php.invalid_component", 400, 400);

        var installed = catalog.Installed.Any(component =>
            string.Equals(component.ComponentId, componentId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(component.Status, "Installed", StringComparison.OrdinalIgnoreCase));

        if (!installed)
        {
            if (!request.InstallRuntimeIfMissing)
            {
                throw new AppException("php.version_not_installed", 404, 404);
            }

            await phpService.InstallVersionAsync(componentId, actorUserId, ipAddress, cancellationToken);
        }

        return componentId;
    }

    private async Task EnsureComponentAsync(
        string componentId,
        bool installIfMissing,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var installed = catalog.Installed.Any(component =>
            string.Equals(component.ComponentId, componentId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(component.Status, "Installed", StringComparison.OrdinalIgnoreCase));

        if (installed)
        {
            return;
        }

        if (!installIfMissing)
        {
            throw new AppException("appruntime.runtime_not_installed", 404, 404);
        }

        await componentBootstrapper.InstallAsync(componentId, actorUserId, ipAddress, force: false, cancellationToken: cancellationToken);
    }

    private static void WritePhpStarter(string root)
    {
        RemoveDefaultHtml(root);
        WriteStarterIfAbsent(Path.Combine(root, "index.php"), """
            <?php /* adapanel-starter */
            $site = htmlspecialchars($_SERVER['HTTP_HOST'] ?? 'AdaPanel', ENT_QUOTES, 'UTF-8');
            ?><!doctype html>
            <html lang="tr"><meta charset="utf-8"><title><?= $site ?></title>
            <body style="font-family:Segoe UI,sans-serif;background:#09151c;color:#eaf5ee;padding:48px">
            <h1>PHP sitesi yayinda</h1><p>AdaPanel bu siteyi otomatik olarak PHP FastCGI profiline bagladi.</p>
            </body></html>
            """);
        WriteDefaultDocumentConfig(root, "index.php");
    }

    private static void WriteAspNetStarter(string root)
    {
        RemoveDefaultHtml(root);
        WriteStarterIfAbsent(Path.Combine(root, "Default.aspx"), """
            <%@ Page Language="C#" %>
            <!-- adapanel-starter -->
            <!doctype html>
            <html lang="tr"><meta charset="utf-8"><title>AdaPanel ASP.NET</title>
            <body style="font-family:Segoe UI,sans-serif;background:#09151c;color:#eaf5ee;padding:48px">
            <h1>ASP.NET sitesi yayinda</h1><p>CLR v4.0 ve Integrated pipeline otomatik olarak ayarlandi.</p>
            </body></html>
            """);
    }

    private static void WriteClassicAspStarter(string root)
    {
        RemoveDefaultHtml(root);
        WriteStarterIfAbsent(Path.Combine(root, "default.asp"), """
            <!-- adapanel-starter -->
            <!doctype html>
            <html lang="tr"><meta charset="utf-8"><title>AdaPanel Classic ASP</title>
            <body style="font-family:Segoe UI,sans-serif;background:#09151c;color:#eaf5ee;padding:48px">
            <h1>Classic ASP sitesi yayinda</h1><p><%= Now() %></p>
            </body></html>
            """);
    }

    private static void WriteMixedStarter(string root)
    {
        RemoveDefaultHtml(root);
        // Şık karşılama sayfası (Classic ASP ile canlı saat). PHP doğrulaması ayrı php-info.php'de.
        // Kullanıcının kendi dosyalarının ÜZERINE yazma (WriteStarterIfAbsent).
        WriteStarterIfAbsent(Path.Combine(root, "default.asp"), BuildWelcomePage(
            badge: "ASP · ASP.NET · PHP",
            heading: "Siteniz yayında",
            subtitle: "Bu sunucu Classic ASP, ASP.NET ve PHP çalıştırmaya hazır.",
            dynamicLine: "<p class=\"row\"><span class=\"k\">Classic ASP</span><span class=\"v\"><%= Now() %></span></p>",
            extra: "<p class=\"links\"><a href=\"php-info.php\">PHP doğrulaması →</a></p>"));
        WriteStarterIfAbsent(Path.Combine(root, "php-info.php"), BuildPhpWelcomePage());
    }

    /// <summary>Şık, koyu temalı karşılama HTML'i (profesyonel panel benzeri "site hazır" sayfası).</summary>
    private static string BuildWelcomePage(string badge, string heading, string subtitle, string dynamicLine, string extra)
        => $$"""
            <!doctype html>
            <!-- adapanel-starter -->
            <html lang="tr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{heading}}</title>
            <style>
              :root{color-scheme:dark}
              *{box-sizing:border-box}
              body{margin:0;min-height:100vh;display:grid;place-items:center;font-family:"Segoe UI",system-ui,Arial,sans-serif;
                background:radial-gradient(1200px 600px at 50% -10%,#14323a,#0a141a 60%);color:#eaf5ee;padding:24px}
              .card{width:min(640px,100%);padding:46px 44px;border-radius:22px;border:1px solid #21443c;
                background:linear-gradient(180deg,#11242c,#0d1b22);box-shadow:0 30px 90px #0009}
              .badge{display:inline-block;font-size:.74rem;letter-spacing:.16em;text-transform:uppercase;color:#0c1a16;
                background:linear-gradient(90deg,#5fe39a,#37b87a);padding:6px 12px;border-radius:999px;font-weight:800}
              h1{margin:18px 0 8px;font-size:clamp(2rem,6vw,3.2rem);line-height:1.05}
              .sub{color:#9fbcb0;line-height:1.6;margin:0 0 22px}
              .row{display:flex;justify-content:space-between;gap:14px;padding:12px 0;border-top:1px solid #1d3a33;margin:0}
              .k{color:#7fa596}.v{color:#dff6e8;font-variant-numeric:tabular-nums}
              .links{margin-top:22px}
              .links a{color:#5fe39a;text-decoration:none;font-weight:600}
              .links a:hover{text-decoration:underline}
              .foot{margin-top:26px;color:#5d7a70;font-size:.82rem}
            </style></head>
            <body><main class="card">
              <span class="badge">{{badge}}</span>
              <h1>{{heading}}</h1>
              <p class="sub">{{subtitle}}</p>
              {{dynamicLine}}
              {{extra}}
              <p class="foot">AdaPanel · dosyalarınızı bu klasöre yükleyerek bu sayfayı değiştirin.</p>
            </main></body></html>
            """;

    private static string BuildPhpWelcomePage()
        => "<?php /* adapanel-starter */\n"
        + "$v = function_exists('phpversion') ? phpversion() : '';\n"
        + "header('Content-Type: text/html; charset=utf-8');\n"
        + "echo '<!doctype html><html lang=\"tr\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>PHP hazir</title>';\n"
        + "echo '<style>body{margin:0;min-height:100vh;display:grid;place-items:center;font-family:\"Segoe UI\",system-ui,Arial,sans-serif;background:radial-gradient(1200px 600px at 50% -10%,#14323a,#0a141a 60%);color:#eaf5ee;padding:24px}.card{width:min(560px,100%);padding:44px;border-radius:22px;border:1px solid #21443c;background:linear-gradient(180deg,#11242c,#0d1b22);box-shadow:0 30px 90px #0009}.badge{display:inline-block;font-size:.74rem;letter-spacing:.16em;text-transform:uppercase;color:#0c1a16;background:linear-gradient(90deg,#5fe39a,#37b87a);padding:6px 12px;border-radius:999px;font-weight:800}h1{margin:18px 0 8px}p{color:#9fbcb0}code{color:#dff6e8}</style></head><body><main class=\"card\">';\n"
        + "echo '<span class=\"badge\">PHP</span><h1>PHP çalışıyor</h1>';\n"
        + "echo '<p>Sürüm: <code>'.htmlspecialchars($v).'</code></p>';\n"
        /*
         * phpinfo() KALDIRILDI — sadeleştirme değil, BİLGİ İFŞASI düzeltmesi.
         *
         * Eski hâl her yeni PHP sitesine `php-info.php` yazıyordu ve o dosya `?full=1` ile
         * KİMLİK DOĞRULAMASI OLMADAN `phpinfo()` basıyordu. Site DNS'e bağlandığı anda internete
         * açıktır; yol TAHMİN EDİLEBİLİR ("php-info.php") ve panelin açtığı HER sitede aynıdır.
         *
         * phpinfo() çıktısı: mutlak dosya yolları, yüklü eklentiler ve sürümleri, php.ini
         * ayarlarının tamamı, ORTAM DEĞİŞKENLERİ (içinde veritabanı parolası ya da API anahtarı
         * olabilir) ve sunucu yazılımı sürümleri. Bir saldırganın bir sunucuya bakarken ilk
         * aradığı şeydir. Panelin bunu kendiliğinden yayınlaması, müşteriye hiç sorulmadan
         * verilmiş bir karardır.
         *
         * "PHP çalışıyor" sayfası KALDI: kurulumun doğruluğunu göstermek gerçek bir ihtiyaç ve
         * sürüm bilgisi tek başına bu sınıfta değil. Ayrıntılı bilgiye ihtiyacı olan yönetici
         * panelin PHP ekranından bakar — orası kimlik doğrulamalıdır.
         */
        + "echo '</main></body></html>';\n";

    private static void WriteNodeStarter(string root, string startupFile)
    {
        RemoveDefaultHtml(root);
        WriteText(ResolveStarterPath(root, startupFile), """
            const http = require("http");
            const port = Number(process.env.HTTP_PLATFORM_PORT || process.env.PORT || 3000);
            http.createServer((request, response) => {
              response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
              response.end("<h1>Node.js sitesi yayinda</h1><p>AdaPanel runtime profilini otomatik ayarladi.</p>");
            }).listen(port, "127.0.0.1");
            """);
    }

    private static void WritePythonStarter(string root, string startupFile)
    {
        RemoveDefaultHtml(root);
        WriteText(ResolveStarterPath(root, startupFile), """
            import os
            from http.server import BaseHTTPRequestHandler, HTTPServer

            class Handler(BaseHTTPRequestHandler):
                def do_GET(self):
                    body = b"<h1>Python sitesi yayinda</h1><p>AdaPanel runtime profilini otomatik ayarladi.</p>"
                    self.send_response(200)
                    self.send_header("Content-Type", "text/html; charset=utf-8")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)

            port = int(os.environ.get("HTTP_PLATFORM_PORT", os.environ.get("PORT", "8000")))
            HTTPServer(("127.0.0.1", port), Handler).serve_forever()
            """);
    }

    private static void WriteDefaultDocumentConfig(string root, string defaultDocument)
    {
        // profesyonel paneller gibi: varsayılan belge listesini TEMIZLEME (clear yok). Tercih edilen dosyayı
        // (örn. index.php) listenin BAŞINA ekle ama index.html/Default.aspx/index.htm gibi
        // yaygın dosyaları da BIRAK. Böylece kullanıcının index.html'i "/" adresinde çalışmaya
        // devam eder; index.php yoksa 500/403 vermez.
        var webConfig = Path.Combine(root, "web.config");
        if (File.Exists(webConfig) &&
            !File.ReadAllText(webConfig).Contains(StarterMarker, StringComparison.OrdinalIgnoreCase))
        {
            // Kullanıcının kendi web.config'ine dokunma.
            return;
        }

        WriteText(webConfig, $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <!-- {{StarterMarker}} -->
            <configuration>
              <system.webServer>
                <defaultDocument>
                  <files>
                    <add value="{{defaultDocument}}" />
                    <add value="index.php" />
                    <add value="index.html" />
                    <add value="index.htm" />
                    <add value="Default.aspx" />
                    <add value="default.asp" />
                  </files>
                </defaultDocument>
              </system.webServer>
            </configuration>
            """);
    }

    private const string StarterMarker = "adapanel-starter";

    // Yalnız AdaPanel'in KENDI başlangıç index.html'ini kaldırır (işaret içerir). KULLANICININ
    // yüklediği index.html ASLA silinmez (runtime profili değiştirince site bozulmasın).
    private static void RemoveDefaultHtml(string root)
    {
        var indexPath = Path.Combine(root, "index.html");
        if (File.Exists(indexPath) &&
            File.ReadAllText(indexPath).Contains(StarterMarker, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(indexPath);
        }
    }

    // Başlangıç dosyasını yalnızca yoksa ya da AdaPanel'in kendi başlangıcıysa yazar; kullanıcının
    // mevcut dosyasının üzerine YAZMAZ.
    private static void WriteStarterIfAbsent(string path, string content)
    {
        if (File.Exists(path) &&
            !File.ReadAllText(path).Contains(StarterMarker, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        WriteText(path, content);
    }

    private static void WriteText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ResolveStarterPath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("appruntime.path_outside_site_root", 400, 400);
        }

        return candidate;
    }
}
