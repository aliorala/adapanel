using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// HANDLER SIRASI TEK KAYNAKTA. Bu dosyanın kendi yansıma yardımcıları var (Enumerate/InvokeMethod…),
// bu yüzden `using static` ad çakıştırırdı; handler'a özgü üç yardımcı NİTELENEREK çağrılıyor.
// Kopyalanan şey ARTIK yok: AddAt/Add ikilisi ve geri okuma tek yerde yaşıyor.
using IisAccess = AdaPanel.Infrastructure.AspNet.IisConfigurationAccess;

namespace AdaPanel.Infrastructure.Php;

public sealed class PhpService(
    AdaPanelDbContext dbContext,
    IComponentBootstrapper componentBootstrapper,
    IConcurrencyLock concurrencyLock,
    IIisSiteService iisSiteService,
    ILogger<PhpService> logger) : IPhpService
{
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";
    private const string PhpHandlerPrefix = "AdaPanel.Php.";

    public async Task<IReadOnlyCollection<PhpVersionDto>> GetInstalledVersionsAsync(CancellationToken cancellationToken = default)
    {
        var versions = await dbContext.InstalledComponents
            .Where(component => component.ComponentId.StartsWith("php-"))
            .OrderBy(component => component.DisplayName)
            .ToArrayAsync(cancellationToken);

        using var serverManager = TryCreateServerManager();

        return versions.Select(component =>
        {
            var executablePath = ResolvePhpExecutablePath(component.InstallDir);
            var isFastCgiRegistered = false;
            if (serverManager is not null && File.Exists(executablePath))
            {
                try
                {
                    isFastCgiRegistered = IsFastCgiRegistered(serverManager, executablePath);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "FastCGI registration could not be read for PHP component {ComponentId}.",
                        component.ComponentId);
                }
            }

            return new PhpVersionDto
            {
                ComponentId = component.ComponentId,
                DisplayName = component.DisplayName,
                Version = component.Version,
                InstallDir = component.InstallDir,
                ExecutablePath = executablePath,
                Status = component.Status.ToString(),
                IsFastCgiRegistered = isFastCgiRegistered,
                InstalledAtUtc = component.InstalledAtUtc
            };
        }).ToArray();
    }

    public async Task<ComponentInstallResultDto> InstallVersionAsync(
        string componentId,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        EnsurePhpComponentId(componentId);

        var result = await componentBootstrapper.InstallAsync(componentId, actorUserId, ipAddress, force: force, cancellationToken: cancellationToken);
        try
        {
            await RegisterFastCgiAsync(componentId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkPhpConfigurationFailedAsync(componentId, exception, cancellationToken);
            throw;
        }

        return new ComponentInstallResultDto
        {
            ComponentId = result.ComponentId,
            DisplayName = result.DisplayName,
            Version = result.Version,
            VersionMeasured = result.VersionMeasured,
            Status = result.Status,
            InstallDir = result.InstallDir,
            ServiceName = result.ServiceName,
            GeneratedPassword = result.GeneratedPassword,
            Message = "php.install.success"
        };
    }

    public async Task EnsureExtensionsAsync(string componentId, IEnumerable<string> extensions, CancellationToken cancellationToken = default)
    {
        var component = await GetInstalledPhpComponentAsync(componentId, cancellationToken);
        var phpIniPath = Path.Combine(component.InstallDir, "php.ini");
        if (!File.Exists(phpIniPath))
        {
            return; // php.ini yoksa (beklenmedik) sessizce geç; FastCGI kaydı yine yapılır.
        }

        var extensionDirectory = Path.Combine(component.InstallDir, "ext");
        var lines = (await File.ReadAllLinesAsync(phpIniPath, cancellationToken)).ToList();
        var changed = false;

        // extension_dir GÜVENCESİ: php.ini'de AKTİF bir extension_dir yoksa, mutlak ext yoluyla ekle. Yoksa
        // etkinleştirdiğimiz "extension=..." satırları DLL'ler ext/ içinde var olsa bile YÜKLENMEZ (PHP eklentiyi
        // bulamaz) → Roundcube/uygulamalar 500 verir. Aktif bir extension_dir varsa (build/admin değeri) DOKUNMA.
        if (Directory.Exists(extensionDirectory) && EnsureExtensionDir(lines, extensionDirectory))
        {
            changed = true;
        }

        foreach (var extension in extensions)
        {
            // DLL yoksa atla (her PHP derlemesi her eklentiyi getirmez); varsa etkinleştir.
            if (!File.Exists(Path.Combine(extensionDirectory, $"php_{extension}.dll")))
            {
                continue;
            }

            if (EnablePhpIniExtension(lines, extension))
            {
                changed = true;
            }
        }

        if (changed)
        {
            await File.WriteAllLinesAsync(phpIniPath, lines, cancellationToken);

            // php.ini DEĞİŞTİ → sıcak php-cgi süreçlerini yenile ki YENİ ayar (extension_dir + etkinleştirilen
            // eklentiler) HEMEN uygulansın. Aksi halde uygulama (ör. webmail) ilk açılışta hâlâ eski/eksik
            // yapılandırmayı görür → "ikinci denemede/biraz sonra düzeldi" sorunu. IIS php-cgi'yi sonraki istekte
            // taze başlatır (güncel php.ini ile). Best-effort: yenilenemese de bir sonraki havuz dönüşünde uygulanır.
            try
            {
                await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync("taskkill.exe", "/f /im php-cgi.exe", cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "php-cgi yenilenemedi; etkinleştirilen eklentiler bir sonraki havuz dönüşünde uygulanır.");
            }
        }
    }

    // php.ini'de AKTİF bir extension_dir varsa dokunmaz (false); yoksa mutlak ext yoluyla ekler (true). Yorumlu
    // (;extension_dir) satır "aktif yok" sayılır → eklenir. Böylece eklentiler güvenle yüklenir.
    private static bool EnsureExtensionDir(List<string> lines, string extensionDirectory)
    {
        var hasActive = lines.Any(line =>
        {
            var trimmed = line.TrimStart();
            return !trimmed.StartsWith(';') &&
                   trimmed.Replace(" ", string.Empty).StartsWith("extension_dir=", StringComparison.OrdinalIgnoreCase);
        });
        if (hasActive)
        {
            return false;
        }

        // GENEL bölüme (site [PATH=] bloklarından ÖNCE): sona eklenirse yalnız son sitenin
        // eklenti dizini olurdu, öteki siteler eklentileri hiç yükleyemezdi.
        lines.Insert(PhpIniSiteIsolation.GlobalInsertIndex(lines), $"extension_dir = \"{extensionDirectory.Replace('/', '\\')}\"");
        return true;
    }

    // php.ini'de bir eklentiyi etkinleştirir: zaten aktifse false; yorumluysa açar / yoksa ekler → true.
    private static bool EnablePhpIniExtension(List<string> lines, string extension)
    {
        static string Value(string normalized) => normalized["extension=".Length..].Trim().Trim('"');
        bool Matches(string text)
        {
            var normalized = text.Replace(" ", string.Empty);
            if (!normalized.StartsWith("extension=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var value = Value(normalized);
            return string.Equals(value, extension, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, $"php_{extension}.dll", StringComparison.OrdinalIgnoreCase);
        }

        // Zaten aktif (yorumsuz) bir satır varsa dokunma.
        if (lines.Any(line => !line.TrimStart().StartsWith(';') && Matches(line.Trim())))
        {
            return false;
        }

        // Yorumlu bir satır varsa onu aç.
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(';') && Matches(trimmed.TrimStart(';').TrimStart()))
            {
                lines[i] = $"extension={extension}";
                return true;
            }
        }

        lines.Insert(PhpIniSiteIsolation.GlobalInsertIndex(lines), $"extension={extension}");
        return true;
    }

    public async Task RegisterFastCgiAsync(string componentId, CancellationToken cancellationToken = default)
    {
        var component = await GetInstalledPhpComponentAsync(componentId, cancellationToken);
        var executablePath = ResolvePhpExecutablePath(component.InstallDir);
        if (!File.Exists(executablePath))
        {
            throw new AppException("php.executable_not_found", 404, 404);
        }

        try
        {
            await concurrencyLock.RunExclusiveAsync(() =>
            {
                using var serverManager = CreateServerManager();
                EnsureFastCgiApplication(serverManager, executablePath);
                InvokeMethod(serverManager, "CommitChanges");
                return Task.CompletedTask;
            }, cancellationToken);

            using var verificationManager = CreateServerManager();
            if (!IsFastCgiRegistered(verificationManager, executablePath))
            {
                throw new AppException(
                    "php.configuration_failed",
                    500,
                    500,
                    $"FastCGI registration was not persisted for '{executablePath}'.");
            }

            // GÜVENLİK (Plesk varsayılanı): paylaşımlı php.ini'ye expose_php=Off yaz (PHP sürümünü gizle).
            await EnsurePhpSecurityDefaultsAsync(component.InstallDir, cancellationToken);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var mappedException = MapPhpConfigurationException(exception);
            logger.LogError(
                exception,
                "PHP FastCGI registration failed for {ComponentId}. MappedMessageKey={MessageKey}",
                componentId,
                mappedException.MessageKey);
            throw mappedException;
        }
    }

    // GÜVENLİK — Plesk varsayılanı: paylaşımlı php.ini'ye expose_php=Off yazar (sürüm gizlenir; sürüme özel
    // açık avı zorlaşır). disable_functions BİLEREK varsayılan KAPATILMAZ — Plesk de varsayılan kapatmaz;
    // tenant izolasyonu open_basedir (B4, per-site .user.ini) ile sağlanır. Sıkı disable_functions isteyen
    // admin harden-adapanel.ps1 (K3) ile uygular (Plesk'te PHP Settings/Service Plan'la yapıldığı gibi).
    // Idempotent (zaten Off ise yazmaz), best-effort (FastCGI kaydını bozmaz).
    private async Task EnsurePhpSecurityDefaultsAsync(string installDir, CancellationToken cancellationToken)
    {
        try
        {
            var iniPath = Path.Combine(installDir, "php.ini");
            if (!File.Exists(iniPath))
            {
                return;
            }

            var lines = (await File.ReadAllLinesAsync(iniPath, cancellationToken)).ToList();
            // YALNIZ GENEL bölüm okunur: dosya sonundaki site [PATH=] blokları per-site değerler taşır,
            // onları "genel değer" sanmak (ör. son sitenin memory_limit'i) yanlış karar verdirirdi.
            var current = ParseUserIni(PhpIniSiteIsolation.GlobalLines(lines));
            var changed = false;

            // expose_php=Off (Plesk varsayılanı): PHP sürümünü gizle.
            if (!(current.TryGetValue("expose_php", out var exposeValue) &&
                  exposeValue.Trim().Equals("Off", StringComparison.OrdinalIgnoreCase)))
            {
                SetIniDirective(lines, "expose_php", "Off");
                changed = true;
            }

            // memory_limit: stok PHP varsayılanı 128M, modern app'ler (Laravel/WordPress) için sık sık yetersiz
            // ("Allowed memory size exhausted"). Boşsa veya stok 128M ise 256M'ye çıkar; admin DAHA YÜKSEĞE
            // çekmişse DOKUNMA (yalnız boş/128M → 256M, panel genel makul varsayılan).
            var memoryLimit = current.TryGetValue("memory_limit", out var mlValue) ? mlValue.Trim() : string.Empty;
            if (memoryLimit.Length == 0 || memoryLimit.Equals("128M", StringComparison.OrdinalIgnoreCase))
            {
                SetIniDirective(lines, "memory_limit", "256M");
                changed = true;
            }

            if (changed)
            {
                await File.WriteAllLinesAsync(iniPath, lines, cancellationToken);
                logger.LogInformation("PHP varsayılanları uygulandı (expose_php=Off; memory_limit boş/128M ise 256M): {IniPath}", iniPath);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "PHP güvenlik varsayılanı (expose_php) yazılamadı.");
        }
    }

    private async Task MarkPhpConfigurationFailedAsync(
        string componentId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var component = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == componentId, cancellationToken);
        if (component is null)
        {
            return;
        }

        component.Status = ComponentInstallStatus.Failed;
        component.LastError = exception is AppException appException &&
                              !string.IsNullOrWhiteSpace(appException.Details)
            ? $"{appException.MessageKey}\r\n{appException.Details}"
            : exception.Message;
        component.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SitePhpAssignmentDto> SetSitePhpVersionAsync(
        string siteName,
        SetSitePhpVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        var component = await GetInstalledPhpComponentAsync(request.ComponentId, cancellationToken);
        var executablePath = ResolvePhpExecutablePath(component.InstallDir);
        if (!File.Exists(executablePath))
        {
            throw new AppException("php.executable_not_found", 404, 404);
        }

        var handlerName = BuildHandlerName(component.ComponentId);

        try
        {
            var assignment = await concurrencyLock.RunExclusiveAsync(() =>
            {
                bool basaKondu;
                using (var serverManager = CreateServerManager())
                {
                    EnsureSiteExists(serverManager, siteName);
                    EnsureFastCgiApplication(serverManager, executablePath);
                    basaKondu = SetSitePhpHandler(serverManager, siteName, executablePath, handlerName);
                    InvokeMethod(serverManager, "CommitChanges");
                }

                // COMMIT'TEN SONRA TAZE OKUMA. Yukarıdaki nesne grafiği BELLEKTEKİ hâli anlatır;
                // ".php gerçekten çalışacak mı" sorusunun cevabı ancak diskten okunan sırada.
                // Ölçüm düşerse yazma başarısız SAYILMAZ — ölçüm "OLÇÜLEMEDİ"ye düşer.
                var siralama = HandlerOrderMeasurement.Olculemedi(basaKondu);
                try
                {
                    using var freshServerManager = CreateServerManager();
                    siralama = HandlerOrderMeasurement.Olc(
                        IisAccess.SnapshotHandlers(IisAccess.GetHandlersCollection(freshServerManager, siteName)),
                        handlerName,
                        basaKondu);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "PHP handler sırası commit sonrası ÖLÇÜLEMEDİ: {Site}", siteName);
                }

                return Task.FromResult(new SitePhpAssignmentDto
                {
                    SiteName = siteName,
                    ComponentId = component.ComponentId,
                    DisplayName = component.DisplayName,
                    Version = component.Version,
                    ExecutablePath = executablePath,
                    HandlerName = handlerName,
                    IsFastCgiRegistered = true,
                    HandlerOrder = siralama
                });
            }, cancellationToken);

            // PHP atanan siteye panel varsayılanı .user.ini (limitler) — yoksa yazılır, varsa dokunulmaz.
            await EnsureDefaultUserIniAsync(siteName, cancellationToken);

            // KİRACI İZOLASYONU VARSAYILAN AÇIK: siteye PHP verildiği anda paylaşılan php.ini'ye [PATH=] bloğu
            // yazılır. Zaten açıksa idempotent yeniden yazımdır (kök değişmişse — belge kökü/Laravel yükseltmesi —
            // güncellenmiş olur). Best-effort: izolasyon yazılamadıysa PHP ATAMASI BAŞARISIZ SAYILMAZ, ama
            // uyarı GÜNLÜĞE düşer; sessizce "izole" denmez (panel GetSitePhpSettings ile gerçeği okur).
            try
            {
                await EnsureSiteIsolationAsync(siteName, enabled: true, cancellationToken);
            }
            catch (Exception isolationException)
            {
                logger.LogWarning(isolationException, "Site PHP izolasyonu yazılamadı (en iyi çaba): {Site}", siteName);
            }

            return assignment;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var mappedException = MapPhpConfigurationException(exception);
            logger.LogError(
                exception,
                "PHP assignment failed for site {SiteName} and component {ComponentId}. MappedMessageKey={MessageKey}",
                siteName,
                request.ComponentId,
                mappedException.MessageKey);
            throw mappedException;
        }
    }

    public async Task<SitePhpSettingsDto> GetSitePhpSettingsAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var dto = new SitePhpSettingsDto { SiteName = site.Name };
        var iniPath = Path.Combine(site.PhysicalPath, ".user.ini");

        if (File.Exists(iniPath))
        {
            dto.Configured = true;
            var values = ParseUserIni(await File.ReadAllLinesAsync(iniPath, cancellationToken));
            if (values.TryGetValue("memory_limit", out var ml)) dto.MemoryLimit = ml;
            if (values.TryGetValue("max_execution_time", out var met) && int.TryParse(met, out var metVal)) dto.MaxExecutionTime = metVal;
            if (values.TryGetValue("upload_max_filesize", out var umf)) dto.UploadMaxFilesize = umf;
            if (values.TryGetValue("post_max_size", out var pms)) dto.PostMaxSize = pms;
            if (values.TryGetValue("display_errors", out var de))
            {
                dto.DisplayErrors = de.Equals("On", StringComparison.OrdinalIgnoreCase) || de == "1";
            }
        }

        // İZOLASYON DURUMU .user.ini'DEN OKUNMAZ. Tek kaynak paylaşılan php.ini'deki [PATH=] bloğudur;
        // .user.ini kiracının silebildiği bir dosyadır, oradan okumak "açık" diye yalan söyleyebilirdi.
        dto.OpenBasedirEnabled = await IsSiteIsolatedAsync(site.Name, cancellationToken);
        return dto;
    }

    /// <summary>Sitenin izolasyon bloğu KURULU PHP'lerin herhangi birinin php.ini'sinde var mı.</summary>
    private async Task<bool> IsSiteIsolatedAsync(string siteName, CancellationToken cancellationToken)
    {
        foreach (var iniPath in await GetPhpIniPathsAsync(cancellationToken))
        {
            var lines = await File.ReadAllLinesAsync(iniPath, cancellationToken);
            if (PhpIniSiteIsolation.IsEnabled(lines, siteName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Kurulu her PHP sürümünün php.ini yolu (site bir sürümden ötekine geçebilir → hepsine yazılır).</summary>
    private async Task<IReadOnlyList<string>> GetPhpIniPathsAsync(CancellationToken cancellationToken)
    {
        var components = await dbContext.InstalledComponents
            .Where(component => component.ComponentId.StartsWith("php-"))
            .ToArrayAsync(cancellationToken);

        return components
            .Select(component => Path.Combine(component.InstallDir, "php.ini"))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Sitenin KENDİ app pool kimliği bir dizine yazabilsin (oturum/yükleme/temp dosyaları site-içi tmp'e düşer).
    //
    // NEDEN IIS_IUSRS DEĞİL: IIS_IUSRS TÜM app pool sanal hesaplarını kapsar. Site kökünün kalıtımı
    // SiteProvisioningService.IsolateSiteAsync ile kırılıp yalnız kendi havuzuna açılıyor; tmp'e IIS_IUSRS
    // vermek tam da izole edilen yeri (oturum çerezleri + yüklenen dosyalar) BÜTÜN kiracılara geri açardı.
    private static async Task GrantAppPoolWriteAsync(string directory, string applicationPoolName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(applicationPoolName))
        {
            // Havuz adı okunamadıysa UYDURMA: yanlış hesaba izin vermek icacls'te sessiz 1332'dir.
            return;
        }

        try
        {
            await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync(
                "icacls.exe",
                $"\"{directory}\" /grant \"IIS AppPool\\{applicationPoolName}:(OI)(CI)M\" /T /C /Q",
                cancellationToken: cancellationToken);
        }
        catch
        {
            // best-effort: ayar yine yazılır, dizin kök kalıtımından da yazılabilir olabilir.
        }
    }

    /// <summary>
    /// Site başına kiracı izolasyonunu (open_basedir + site-içi temp/oturum) paylaşılan php.ini'ye yazar
    /// ya da kaldırır. YENİ SİTELERDE VARSAYILAN AÇIK — çağrı yeri SiteProvisioningService.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mekanizma ve ölçümü için bkz. <see cref="PhpIniSiteIsolation"/>. Burada yalnız IO var:
    /// kök seçimi, tmp dizini, izin ve KURULU HER PHP sürümünün php.ini'si.
    /// </para>
    /// <para>
    /// <b>Panelin kendi araç siteleri (AdaPanel.*) hariç:</b> phpMyAdmin/Adminer/Roundcube kiracı değil,
    /// panelin parçasıdır; kendi kurulum ağaçları (<c>tools\</c>) site kökünün DIŞINDADIR ve bu depoda
    /// araç sitelerini sıkılaştırmak üç ayrı sürümde canlıda kırdı. Aynı istisna
    /// SiteProvisioningService.IsolateSiteAsync (NTFS katmanı) için de geçerlidir.
    /// </para>
    /// <para>
    /// <b>Yeni php.ini ne zaman okunur:</b> php-cgi php.ini'yi SÜREÇ BAŞINDA okur ve FastCGI süreçleri
    /// (app pool + yol) başına ayrılır. Yeni bir sitenin havuzunda henüz süreç YOKTUR → blok ilk istekte
    /// zaten uygulanır, yeniden başlatma gerekmez. Var olan bir sitede ayar değiştirildiğinde çağıran
    /// taraf php-cgi'yi yeniler (UpdateSitePhpSettingsAsync).
    /// </para>
    /// </remarks>
    public async Task EnsureSiteIsolationAsync(string siteName, bool enabled, CancellationToken cancellationToken = default)
    {
        if (siteName.StartsWith(AdaPanel.Infrastructure.Iis.ManagedAppPoolName.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var iniPaths = await GetPhpIniPathsAsync(cancellationToken);
        if (iniPaths.Count == 0)
        {
            // PHP kurulu değilse yazacak php.ini de yok. Site PHP aldığında bu metot yeniden çağrılır.
            return;
        }

        string? root = null;
        if (enabled)
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            root = Path.GetFullPath(string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                ? site.PhysicalPath
                : site.ApplicationRootPath);

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                throw new AppException("php.site_root_missing", 404, 404);
            }

            // Oturum + yükleme + sys temp bu dizine bağlanır; yoksa PHP oturum açamaz (500).
            var tempDirectory = PhpIniSiteIsolation.TempDirectoryFor(root);
            Directory.CreateDirectory(tempDirectory);
            await GrantAppPoolWriteAsync(tempDirectory, site.ApplicationPoolName, cancellationToken);

            // WordPress gibi düz kurulumlarda kök = belge köküdür; koruma olmadan oturum dosyaları
            // /tmp/sess_... adresinden İNDİRİLEBİLİRDİ. Mevcut bir web.config EZİLMEZ (yönetici
            // oraya bilerek bir şey koymuş olabilir); yalnız yoksa yazılır.
            var tempWebConfig = Path.Combine(tempDirectory, "web.config");
            if (!File.Exists(tempWebConfig))
            {
                await File.WriteAllTextAsync(
                    tempWebConfig,
                    PhpIniSiteIsolation.TempDirectoryWebConfig,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken);
            }
        }

        foreach (var iniPath in iniPaths)
        {
            var lines = (await File.ReadAllLinesAsync(iniPath, cancellationToken)).ToList();
            var updated = enabled
                ? PhpIniSiteIsolation.Upsert(lines, siteName, root!)
                : PhpIniSiteIsolation.Remove(lines, siteName);
            await File.WriteAllLinesAsync(iniPath, updated, cancellationToken);
        }

        logger.LogInformation(
            "Site PHP izolasyonu {Durum}: {Site} ({Sayi} php.ini)",
            enabled ? "yazıldı" : "kaldırıldı",
            siteName,
            iniPaths.Count);
    }

    // PHP atanan siteye .user.ini YOKsa panel varsayılanlarını yazar (limitler). İZOLASYON BURADA DEĞİL:
    // open_basedir/temp/oturum yolları paylaşılan php.ini'nin [PATH=] bloğundadır (bkz. PhpIniSiteIsolation) —
    // .user.ini kiracının silebildiği bir dosyadır, güvenlik sınırı oraya konmaz.
    // Mevcut .user.ini'ye DOKUNMAZ (kullanıcı/önceki ayar korunur). Best-effort: hata atama akışını bozmaz.
    private async Task EnsureDefaultUserIniAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            if (string.IsNullOrWhiteSpace(site.PhysicalPath) || !Directory.Exists(site.PhysicalPath))
            {
                return;
            }

            var iniPath = Path.Combine(site.PhysicalPath, ".user.ini");
            if (File.Exists(iniPath))
            {
                return;
            }

            var content = new StringBuilder();
            content.AppendLine("; AdaPanel - site PHP ayarları (.user.ini, per-dizin)");
            content.AppendLine("memory_limit = 256M");
            content.AppendLine("max_execution_time = 120");
            content.AppendLine("upload_max_filesize = 64M");
            content.AppendLine("post_max_size = 64M");
            content.AppendLine("display_errors = Off");
            await File.WriteAllTextAsync(iniPath, content.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Varsayılan .user.ini yazılamadı: {Site}", siteName);
        }
    }

    public async Task<SitePhpSettingsDto> UpdateSitePhpSettingsAsync(
        string siteName,
        UpdateSitePhpSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        if (string.IsNullOrWhiteSpace(site.PhysicalPath) || !Directory.Exists(site.PhysicalPath))
        {
            throw new AppException("php.site_root_missing", 404, 404);
        }

        var content = new StringBuilder();
        content.AppendLine("; AdaPanel - site PHP ayarları (.user.ini, per-dizin)");
        content.AppendLine($"memory_limit = {SanitizeIniValue(request.MemoryLimit)}");
        content.AppendLine($"max_execution_time = {Math.Clamp(request.MaxExecutionTime, 0, 86400)}");
        content.AppendLine($"upload_max_filesize = {SanitizeIniValue(request.UploadMaxFilesize)}");
        content.AppendLine($"post_max_size = {SanitizeIniValue(request.PostMaxSize)}");
        content.AppendLine($"display_errors = {(request.DisplayErrors ? "On" : "Off")}");

        var iniPath = Path.Combine(site.PhysicalPath, ".user.ini");
        await File.WriteAllTextAsync(iniPath, content.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        // İZOLASYON: istek bunu SÖYLEMİYORSA (null) mevcut durum KORUNUR.
        //
        // ÖLÇÜLMÜŞ TUZAK SINIFI — alan eskiden `bool` idi ve varsayılanı `false`'du: alanı hiç göndermeyen
        // eski bir arayüz ya da API istemcisi, "limitleri kaydet" derken izolasyonu SESSİZCE KAPATIRDI.
        // Bu depoda aynı sınıf birden çok kez ölçüldü; bu yüzden alan `bool?` ve "söylenmediyse dokunma".
        if (request.OpenBasedirEnabled is { } isolationRequested)
        {
            await EnsureSiteIsolationAsync(site.Name, isolationRequested, cancellationToken);

            // php.ini süreç başında okunur: var olan bir sitede değişikliğin HEMEN geçerli olması için
            // php-cgi yenilenir (UpdateGlobalPhpSettingsAsync ile aynı, ölçülmüş yöntem). Best-effort.
            try
            {
                await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync("taskkill.exe", "/f /im php-cgi.exe", cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "php-cgi yenilenemedi; izolasyon değişikliği bir sonraki havuz dönüşünde uygulanır.");
            }
        }

        // KRİTİK: PHP upload limitini yükseltmek tek başına yetmez — IIS maxAllowedContentLength varsayılanı
        // ~30MB'de "413 Request Entity Too Large" verir. post_max_size'ı baz alıp IIS limitini de yükselt
        // ki panel ayarı uçtan uca çalışsın (phpMyAdmin SQL import dahil).
        var uploadBytes = ParsePhpSize(request.PostMaxSize);
        if (uploadBytes > 0)
        {
            AdaPanel.Infrastructure.Iis.WebAppPublisher.SetMaxAllowedContentLength(site.PhysicalPath, uploadBytes);
        }

        // max_execution_time panelden değiştirilebilir; FastCGI requestTimeout/activityTimeout'u da en az o kadar
        // yükselt ki uzun işlemler (büyük import vb.) "500 timeout (0x80070102)" vermesin. Raise-only, global FastCGI.
        await RaiseFastCgiTimeoutAsync(FastCgiSecondsFor(request.MaxExecutionTime), cancellationToken);

        return await GetSitePhpSettingsAsync(siteName, cancellationToken);
    }

    public async Task<SitePhpSettingsDto> GetGlobalPhpSettingsAsync(CancellationToken cancellationToken = default)
    {
        var component = await GetLatestInstalledPhpAsync(cancellationToken);
        var dto = new SitePhpSettingsDto { SiteName = "global" };
        if (component is null)
        {
            return dto;
        }

        var iniPath = Path.Combine(component.InstallDir, "php.ini");
        if (File.Exists(iniPath))
        {
            dto.Configured = true;
            // Site [PATH=] blokları hariç: genel ekran genel değerleri göstermeli.
            var values = ParseUserIni(PhpIniSiteIsolation.GlobalLines(await File.ReadAllLinesAsync(iniPath, cancellationToken)));
            if (values.TryGetValue("memory_limit", out var ml)) dto.MemoryLimit = ml;
            if (values.TryGetValue("max_execution_time", out var met) && int.TryParse(met, out var metVal)) dto.MaxExecutionTime = metVal;
            if (values.TryGetValue("upload_max_filesize", out var umf)) dto.UploadMaxFilesize = umf;
            if (values.TryGetValue("post_max_size", out var pms)) dto.PostMaxSize = pms;
        }

        // phpredis durumu: kart "etkin ise etkin yazsın" (kullanıcı isteği). php -m ile gerçekten yüklü mü bak (best-effort).
        try
        {
            var phpCli = Path.Combine(component.InstallDir, "php.exe");
            if (!File.Exists(phpCli))
            {
                phpCli = ResolvePhpExecutablePath(component.InstallDir); // php-cgi.exe yedeği
            }

            if (File.Exists(phpCli))
            {
                dto.RedisEnabled = await PhpHasModuleAsync(phpCli, "redis", cancellationToken);
            }
        }
        catch
        {
            // Durum belirlenemezse false kalır (UI "Devre dışı" gösterir, etkinleştir butonu görünür).
        }

        return dto;
    }

    public async Task<SitePhpSettingsDto> UpdateGlobalPhpSettingsAsync(
        UpdateSitePhpSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var component = await GetLatestInstalledPhpAsync(cancellationToken)
            ?? throw new AppException("php.version_not_installed", 404, 404);
        var iniPath = Path.Combine(component.InstallDir, "php.ini");
        if (!File.Exists(iniPath))
        {
            throw new AppException("php.version_not_installed", 404, 404);
        }

        var lines = (await File.ReadAllLinesAsync(iniPath, cancellationToken)).ToList();
        SetIniDirective(lines, "memory_limit", SanitizeIniValue(request.MemoryLimit));
        SetIniDirective(lines, "max_execution_time", Math.Clamp(request.MaxExecutionTime, 0, 86400).ToString());
        SetIniDirective(lines, "upload_max_filesize", SanitizeIniValue(request.UploadMaxFilesize));
        SetIniDirective(lines, "post_max_size", SanitizeIniValue(request.PostMaxSize));
        await File.WriteAllLinesAsync(iniPath, lines, cancellationToken);

        // Süre (max_execution_time) değişince FastCGI requestTimeout'u da yükselt (yoksa büyük import 500 timeout verir).
        await RaiseFastCgiTimeoutAsync(FastCgiSecondsFor(request.MaxExecutionTime), cancellationToken);

        // php-cgi süreçlerini yenile ki yeni php.ini HEMEN uygulansın (FastCGI php.ini'yi süreç başında okur). Best-effort.
        try
        {
            await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync("taskkill.exe", "/f /im php-cgi.exe", cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "php-cgi yeniden başlatılamadı; global PHP ayarı bir sonraki havuz dönüşünde uygulanır.");
        }

        return await GetGlobalPhpSettingsAsync(cancellationToken);
    }

    public async Task<RedisExtensionResultDto> EnableRedisExtensionAsync(CancellationToken cancellationToken = default)
    {
        var component = await GetLatestInstalledPhpAsync(cancellationToken)
            ?? throw new AppException("php.not_installed", 404, 404);

        var phpCli = Path.Combine(component.InstallDir, "php.exe");
        if (!File.Exists(phpCli))
        {
            phpCli = ResolvePhpExecutablePath(component.InstallDir); // php-cgi.exe yedeği
        }
        if (!File.Exists(phpCli))
        {
            throw new AppException("php.executable_not_found", 404, 404);
        }

        // Zaten yüklüyse tekrar indirme.
        if (await PhpHasModuleAsync(phpCli, "redis", cancellationToken))
        {
            return new RedisExtensionResultDto { Enabled = true, Message = "already" };
        }

        var (minor, ts, arch) = await DetectPhpBuildAsync(phpCli, cancellationToken);
        var vs = Version.TryParse(minor + ".0", out var parsed) && parsed >= new Version(8, 4) ? "vs17" : "vs16";
        const string redisVersion = "6.1.0";
        var fileName = $"php_redis-{redisVersion}-{minor}-{ts}-{vs}-{arch}.zip";
        var url = $"https://downloads.php.net/~windows/pecl/releases/redis/{redisVersion}/{fileName}";

        var extDir = Path.Combine(component.InstallDir, "ext");
        Directory.CreateDirectory(extDir);
        var dllPath = Path.Combine(extDir, "php_redis.dll");
        var tempZip = Path.Combine(Path.GetTempPath(), $"adaredis-{Guid.NewGuid():N}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"adaredis-{Guid.NewGuid():N}");

        var script =
            "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; " +
            $"Invoke-WebRequest -Uri '{url}' -OutFile '{tempZip.Replace("'", "''")}' -UseBasicParsing; " +
            $"Expand-Archive -Path '{tempZip.Replace("'", "''")}' -DestinationPath '{tempDir.Replace("'", "''")}' -Force";
        var download = await AdaPanel.Infrastructure.Support.ProcessRunner.RunPowerShellAsync(script, cancellationToken);

        var sourceDll = Directory.Exists(tempDir)
            ? Directory.EnumerateFiles(tempDir, "php_redis.dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (!download.Success || sourceDll is null)
        {
            SafeDeleteFile(tempZip);
            SafeDeleteDirectory(tempDir);
            throw new AppException("php.redis_download_failed", 502, 502, $"{fileName}: {TruncateError(download.StandardError)}");
        }

        File.Copy(sourceDll, dllPath, overwrite: true);
        SafeDeleteFile(tempZip);
        SafeDeleteDirectory(tempDir);

        var iniPath = Path.Combine(component.InstallDir, "php.ini");
        var lines = (await File.ReadAllLinesAsync(iniPath, cancellationToken)).ToList();
        var alreadyDeclared = lines.Any(line =>
        {
            var normalized = line.Trim().TrimStart(';').Replace(" ", string.Empty);
            return normalized.StartsWith("extension=redis", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith("extension=php_redis", StringComparison.OrdinalIgnoreCase);
        });
        if (!alreadyDeclared)
        {
            // GENEL bölüme: site [PATH=] bloklarından sonra eklenirse redis yalnız son sitede yüklenirdi.
            lines.Insert(PhpIniSiteIsolation.GlobalInsertIndex(lines), "extension=redis");
            await File.WriteAllLinesAsync(iniPath, lines, cancellationToken);
        }

        // DOĞRULA: redis gerçekten yüklendi mi? Hayırsa GERİ AL (DLL sil + extension satırı çıkar) → PHP kırılmaz.
        if (!await PhpHasModuleAsync(phpCli, "redis", cancellationToken))
        {
            var reverted = lines
                .Where(line => !line.Trim().Replace(" ", string.Empty).Equals("extension=redis", StringComparison.OrdinalIgnoreCase))
                .ToList();
            await File.WriteAllLinesAsync(iniPath, reverted, cancellationToken);
            SafeDeleteFile(dllPath);
            throw new AppException("php.redis_verify_failed", 500, 500, $"{fileName} yüklenemedi (PHP build uyumsuz); geri alındı.");
        }

        // Başarılı → php-cgi yenile (FastCGI php.ini'yi süreç başında okur).
        try
        {
            await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync("taskkill.exe", "/f /im php-cgi.exe", cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "php-cgi yeniden başlatılamadı; redis uzantısı bir sonraki havuz dönüşünde aktifleşir.");
        }

        return new RedisExtensionResultDto { Enabled = true, Message = "installed", FileName = fileName };
    }

    private static async Task<bool> PhpHasModuleAsync(string phpExecutable, string moduleName, CancellationToken cancellationToken)
    {
        var result = await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync(
            phpExecutable, "-m", cancellationToken: cancellationToken, timeout: TimeSpan.FromSeconds(30));
        return result.Success && result.StandardOutput
            .Split('\n')
            .Any(line => line.Trim().Equals(moduleName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(string Minor, string Ts, string Arch)> DetectPhpBuildAsync(string phpExecutable, CancellationToken cancellationToken)
    {
        const string code = "echo PHP_MAJOR_VERSION.'.'.PHP_MINOR_VERSION.'|'.(PHP_ZTS?'ts':'nts').'|'.(PHP_INT_SIZE===8?'x64':'x86');";
        var result = await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync(
            phpExecutable, $"-r \"{code}\"", cancellationToken: cancellationToken, timeout: TimeSpan.FromSeconds(30));
        var parts = (result.StandardOutput ?? string.Empty).Trim().Split('|');
        if (parts.Length == 3 && parts[0].Contains('.') && (parts[1] is "ts" or "nts"))
        {
            return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
        }

        // Çözümlenemezse en yaygın IIS/FastCGI build'ine düş (PHP 8.3 NTS x64).
        return ("8.3", "nts", "x64");
    }

    private static string TruncateError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var trimmed = value.Trim();
        return trimmed.Length > 500 ? trimmed[..500] : trimmed;
    }

    private static void SafeDeleteFile(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { /* en iyi çaba */ }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) { Directory.Delete(path, true); } } catch { /* en iyi çaba */ }
    }

    private async Task<InstalledComponent?> GetLatestInstalledPhpAsync(CancellationToken cancellationToken)
    {
        return await dbContext.InstalledComponents
            .Where(item => item.ComponentId.StartsWith("php-") && item.Status == ComponentInstallStatus.Installed)
            .OrderByDescending(item => item.ComponentId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // php.ini'de bir direktifi set eder: aktif varsa değiştirir, yorumluysa açar, yoksa ekler.
    /// <summary>
    /// php.ini'nin GENEL bölümüne bir yönerge yazar.
    /// </summary>
    /// <remarks>
    /// KAPSAM SINIRI ZORUNLU: dosyanın sonunda site başına <c>[PATH=]</c> izolasyon blokları durur
    /// (bkz. <see cref="PhpIniSiteIsolation"/>). Bir <c>[PATH=]</c> başlığından sonraki her satır o
    /// siteye aittir; eski kod bulunamayan yönergeyi dosya SONUNA eklediği için genel ayar son
    /// sitenin bölümüne düşer, genel olarak HİÇ uygulanmazdı. Hem arama hem ekleme ilk PATH/HOST
    /// başlığında durur.
    /// </remarks>
    private static void SetIniDirective(List<string> lines, string name, string value)
    {
        var globalEnd = PhpIniSiteIsolation.GlobalInsertIndex(lines);
        var activeIndex = -1;
        var commentedIndex = -1;
        for (var i = 0; i < globalEnd; i++)
        {
            var trimmed = lines[i].Trim();
            var isComment = trimmed.StartsWith(';');
            var normalized = trimmed.TrimStart(';').TrimStart();
            if (normalized.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                normalized.Length > name.Length &&
                normalized[name.Length..].TrimStart().StartsWith('='))
            {
                if (isComment)
                {
                    if (commentedIndex < 0) { commentedIndex = i; }
                }
                else
                {
                    activeIndex = i;
                    break;
                }
            }
        }

        var formatted = $"{name} = {value}";
        if (activeIndex >= 0) { lines[activeIndex] = formatted; }
        else if (commentedIndex >= 0) { lines[commentedIndex] = formatted; }
        else { lines.Insert(globalEnd, formatted); }
    }

    // "256M" / "1G" / "512K" / "12345" → byte. IIS maxAllowedContentLength için PHP boyut değerini çevirir.
    private static long ParsePhpSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        value = value.Trim();
        var unit = char.ToUpperInvariant(value[^1]);
        var hasUnit = unit is 'K' or 'M' or 'G';
        var numberPart = hasUnit ? value[..^1].Trim() : value;
        if (!long.TryParse(numberPart, out var number) || number <= 0)
        {
            return 0;
        }

        return unit switch
        {
            'K' => number * 1024L,
            'M' => number * 1024L * 1024,
            'G' => number * 1024L * 1024 * 1024,
            _ => number
        };
    }

    private static Dictionary<string, string> ParseUserIni(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('['))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            result[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
        }

        return result;
    }

    private static string SanitizeIniValue(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^-?\\d+[KMG]?$") ? trimmed : "0";
    }

    private async Task<InstalledComponent> GetInstalledPhpComponentAsync(string componentId, CancellationToken cancellationToken)
    {
        EnsurePhpComponentId(componentId);

        var component = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == componentId, cancellationToken);

        if (component is null || !string.Equals(component.Status.ToString(), "Installed", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("php.version_not_installed", 404, 404);
        }

        return component;
    }

    private static void EnsurePhpComponentId(string componentId)
    {
        if (!componentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("php.invalid_component", 400, 400);
        }
    }

    private static string ResolvePhpExecutablePath(string installDir)
    {
        return Path.Combine(installDir, "php-cgi.exe");
    }

    private static string BuildHandlerName(string componentId)
    {
        return PhpHandlerPrefix + componentId.Replace(' ', '-');
    }

    private static IDisposable CreateServerManager()
    {
        var assemblyPath = Environment.ExpandEnvironmentVariables(WebAdministrationAssemblyPath);
        if (!File.Exists(assemblyPath))
        {
            throw new AppException("sites.iis_not_available", 503, 503);
        }

        var assembly = Assembly.LoadFrom(assemblyPath);
        var serverManagerType = assembly.GetType(ServerManagerTypeName)
            ?? throw new AppException("sites.iis_not_available", 503, 503);

        return Activator.CreateInstance(serverManagerType) as IDisposable
            ?? throw new AppException("sites.iis_not_available", 503, 503);
    }

    private static IDisposable? TryCreateServerManager()
    {
        try
        {
            return CreateServerManager();
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureSiteExists(IDisposable serverManager, string siteName)
    {
        var sites = GetPropertyObject(serverManager, "Sites");
        var site = Enumerate(sites).FirstOrDefault(item =>
            string.Equals(GetStringProperty(item, "Name"), siteName, StringComparison.OrdinalIgnoreCase));

        if (site is null)
        {
            throw new AppException("sites.site_not_found", 404, 404);
        }
    }

    private static bool IsFastCgiRegistered(IDisposable serverManager, string executablePath)
    {
        var fastCgiCollection = GetFastCgiCollection(serverManager);
        return Enumerate(fastCgiCollection).Any(item =>
            string.Equals(GetAttributeString(item, "fullPath"), executablePath, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureFastCgiApplication(IDisposable serverManager, string executablePath)
    {
        var fastCgiCollection = GetFastCgiCollection(serverManager);
        var existing = Enumerate(fastCgiCollection).FirstOrDefault(item =>
            string.Equals(GetAttributeString(item, "fullPath"), executablePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // KRİTİK: FastCGI requestTimeout varsayılanı 90sn, activityTimeout 30sn — PHP max_execution_time'dan
            // (300sn) KISA. Büyük SQL import / uzun işlemler "500 0x80070102 (timeout)" verir. Mevcut kayıtta da yükselt.
            SetAttributeValue(existing, "activityTimeout", 600);
            SetAttributeValue(existing, "requestTimeout", 600);
            // B4 (sertleştirme): bir php-cgi süreci 10000 istekten sonra geri dönüştürülür. Kaçak bellek
            // biriktiren / uzun ömürlü süreçlerin "blast radius"unu sınırlar; PHP'nin kendi PHP_FCGI_MAX_REQUESTS
            // varsayılanıyla (10000) hizalı olduğundan PHP süreci önce kendini sonlandırıp 500 vermez.
            SetAttributeValue(existing, "instanceMaxRequests", 10000);
            return;
        }

        var application = InvokeMethod(fastCgiCollection, "CreateElement", "application");
        SetAttributeValue(application, "fullPath", executablePath);
        SetAttributeValue(application, "arguments", string.Empty);
        SetAttributeValue(application, "activityTimeout", 600);
        SetAttributeValue(application, "requestTimeout", 600);
        // B4: süreç geri-dönüşüm sınırı (yukarıdaki açıklama). maxInstances'a DOKUNULMAZ -> IIS otomatik
        // (0) bırakılır; sabit düşük bir tavan meşru eşzamanlılığı boğardı.
        SetAttributeValue(application, "instanceMaxRequests", 10000);
        InvokeMethod(fastCgiCollection, "Add", application);
    }

    // Tüm FastCGI uygulamalarının requestTimeout/activityTimeout'unu en az `seconds` yapar (düşürmez).
    // Panelden max_execution_time yükseltilince çağrılır → FastCGI önce kesip 500 vermez. Best-effort.
    private async Task RaiseFastCgiTimeoutAsync(int seconds, CancellationToken cancellationToken)
    {
        try
        {
            await concurrencyLock.RunExclusiveAsync(() =>
            {
                using var serverManager = CreateServerManager();
                var collection = GetFastCgiCollection(serverManager);
                var changed = false;
                foreach (var app in Enumerate(collection))
                {
                    changed |= RaiseTimeoutAttribute(app, "requestTimeout", seconds);
                    changed |= RaiseTimeoutAttribute(app, "activityTimeout", seconds);
                }

                if (changed)
                {
                    InvokeMethod(serverManager, "CommitChanges");
                }

                return Task.CompletedTask;
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "FastCGI timeout yükseltilemedi (best-effort).");
        }
    }

    private static bool RaiseTimeoutAttribute(object application, string name, int seconds)
    {
        int.TryParse(GetAttributeString(application, name), out var current);
        if (current >= seconds)
        {
            return false;
        }

        SetAttributeValue(application, name, seconds);
        return true;
    }

    // max_execution_time 0 ise PHP'de SÜRESİZ demektir; ama IIS FastCGI sınırsız olamaz → 24 saat (86400) tavan.
    // Aksi halde "süresiz PHP + kısa FastCGI" durumunda uzun işlem yine 500 timeout verirdi.
    private static int FastCgiSecondsFor(int maxExecutionTime)
        => maxExecutionTime <= 0 ? 86400 : Math.Clamp(maxExecutionTime, 30, 86400) + 60;

    /// <summary>Handler BAŞA konabildiyse true; sona eklendiyse false (kısmi başarı).</summary>
    private static bool SetSitePhpHandler(IDisposable serverManager, string siteName, string executablePath, string handlerName)
    {
        var handlers = IisAccess.GetHandlersCollection(serverManager, siteName);

        foreach (var existing in Enumerate(handlers).Where(item =>
                     GetAttributeString(item, "name").StartsWith(PhpHandlerPrefix, StringComparison.OrdinalIgnoreCase))
                 .ToArray())
        {
            InvokeMethod(handlers, "Remove", existing);
        }

        var handler = InvokeMethod(handlers, "CreateElement", "add");
        SetAttributeValue(handler, "name", handlerName);
        SetAttributeValue(handler, "path", "*.php");
        SetAttributeValue(handler, "verb", "GET,HEAD,POST");
        SetAttributeValue(handler, "modules", "FastCgiModule");
        SetAttributeValue(handler, "scriptProcessor", executablePath);
        SetAttributeValue(handler, "resourceType", "Either");
        SetAttributeValue(handler, "requireAccess", "Script");

        // KRİTİK: handler'ı listenin BAŞINA ekle. IIS handler eşleştirmesini yukarıdan
        // aşağıya yapar ve ilk eşleşeni kullanır. Devralınan StaticFile (path="*") tüm
        // istekleri yakaladığı için *.php handler sona eklenirse StaticFile önce eşleşir ve
        // .php dosyaları "404.3 - extension not configured" döner (phpMyAdmin/Adminer açılmaz).
        // AddAt(0, ...) ile *.php FastCGI handler StaticFile'dan ÖNCE değerlendirilir.
        //
        // FALLBACK ARTIK SESSİZ DEĞİL. Eskiden burada çıplak bir catch vardı ve handler'ı SONA
        // ekliyordu — yani yorumun az yukarıda "404.3 verir" diye tarif ettiği sıralamayı
        // "çalışmasını garantiye al" diyerek uyguluyordu. Sona eklemek 404.3'ü ÜRETEN
        // sıralamadır. Fallback korunuyor (hiç yazmamaktan iyidir) ama artık İŞARETLENİYOR:
        // dönen değer çağırana gider, çağıran commit'ten sonra gerçek sırayı ÖLÇER.
        return IisAccess.InsertHandlerFirst(handlers, handler);
    }

    private static object GetFastCgiCollection(IDisposable serverManager)
    {
        var configuration = InvokeMethod(serverManager, "GetApplicationHostConfiguration");
        var section = InvokeMethod(configuration, "GetSection", "system.webServer/fastCgi");
        return GetConfigurationCollection(section);
    }

    private static IEnumerable<object> Enumerate(object collection)
    {
        return collection is IEnumerable enumerable
            ? enumerable.Cast<object>()
            : [];
    }

    private static object GetPropertyObject(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            return property.GetValue(target)
                ?? throw new AppException("php.configuration_failed", 500, 500);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static object GetConfigurationCollection(object section)
    {
        var property = section.GetType().GetProperty("Collection");
        if (property is not null)
        {
            try
            {
                var collection = property.GetValue(section);
                if (collection is not null)
                {
                    return collection;
                }
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        return InvokeMethod(section, "GetCollection");
    }

    private static string GetStringProperty(object target, string propertyName)
    {
        return GetPropertyObject(target, propertyName).ToString() ?? string.Empty;
    }

    private static string GetAttributeString(object target, string attributeName)
    {
        return InvokeMethod(target, "GetAttributeValue", attributeName).ToString() ?? string.Empty;
    }

    private static void SetAttributeValue(object target, string attributeName, object? value)
    {
        InvokeMethod(target, "SetAttributeValue", attributeName, value);
    }

    private static object InvokeMethod(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length == arguments.Length &&
                AreParametersCompatible(candidate.GetParameters(), arguments));

        if (method is null)
        {
            throw new MissingMethodException(target.GetType().FullName, methodName);
        }

        try
        {
            return method.Invoke(target, arguments)
                ?? new object();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static AppException MapPhpConfigurationException(Exception exception)
    {
        // Dile bağımsız (HRESULT + İngilizce) sınıflandırma — Türkçe Windows'ta da doğru çalışır.
        var message = Support.Win32ErrorHelper.Flatten(exception);
        if (Support.Win32ErrorHelper.IsAccessDenied(message))
        {
            return CreateDiagnosticException("sites.iis_permission_denied", 403, exception);
        }

        if (message.Contains("section", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("FastCgiModule", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("fastCgi", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.iis_install_required", 503, exception);
        }

        return CreateDiagnosticException("php.configuration_failed", 500, exception);
    }

    private static AppException CreateDiagnosticException(string messageKey, int statusCode, Exception exception)
    {
        return new AppException(messageKey, statusCode, statusCode, BuildDiagnosticDetails(exception));
    }

    private static string BuildDiagnosticDetails(Exception exception)
    {
        var details = exception.ToString();
        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private static string FlattenExceptionMessage(Exception exception)
    {
        var builder = new StringBuilder(exception.Message);
        var current = exception.InnerException;
        while (current is not null)
        {
            builder.Append(' ').Append(current.Message);
            current = current.InnerException;
        }

        return builder.ToString();
    }

    private static bool AreParametersCompatible(ParameterInfo[] parameters, object?[] arguments)
    {
        for (var index = 0; index < parameters.Length; index += 1)
        {
            var parameterType = parameters[index].ParameterType;
            var argument = arguments[index];

            if (argument is null)
            {
                if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null)
                {
                    return false;
                }

                continue;
            }

            if (parameterType.IsInstanceOfType(argument))
            {
                continue;
            }

            if (parameterType.IsEnum && argument is IConvertible)
            {
                continue;
            }

            if (typeof(IConvertible).IsAssignableFrom(parameterType) && argument is IConvertible)
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
