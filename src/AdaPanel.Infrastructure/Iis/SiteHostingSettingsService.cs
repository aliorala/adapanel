using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.SiteBuilder;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Iis;

public sealed class SiteHostingSettingsService(
    IIisSiteService iisSiteService,
    IComponentBootstrapper componentBootstrapper,
    ISchedulerService schedulerService,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    IConcurrencyLock concurrencyLock,
    JsonFileStore fileStore,
    // İSTEĞE BAĞLI: DENETLENEN giden istemci. Arşiv indirmesi buradan geçer (bkz.
    // ImportSiteArchiveAsync); verilmezse http/https indirme YAPILMAZ, sessizce eski
    // denetimsiz yola düşmez.
    IHttpClientFactory? httpClientFactory = null,
    // İSTEĞE BAĞLI: adres çözümleyici. Testte sahte — birim testi gerçek DNS'e çıkmamalı
    // (aynı kalıp GitDeploymentService ve SiteMigrationService'te de var).
    Func<string, CancellationToken, Task<IPAddress[]>>? outboundResolver = null) : ISiteHostingSettingsService
{
    private readonly string dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));
    private const string StatePath = "config/site-hosting-settings.json";
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string HstsHeaderName = "Strict-Transport-Security";
    internal Func<Stream, Stream>? ConfigurationStream { get; init; }

    // ".woff2" gibi: nokta + harf/rakam ile başlar, ayırıcı/boşluk/yol karakteri içermez.
    private static readonly Regex MimeExtensionPattern = new(@"^\.[a-z0-9][a-z0-9._-]{0,31}$", RegexOptions.Compiled);

    // "font/woff2" ya da "text/html; charset=utf-8". RFC 6838 token kümesinin güvenli bir alt kümesi:
    // tırnak, açılı parantez ve noktalı virgül (parametre ayırıcısı dışında) kasten dışarıda.
    private static readonly Regex MimeTypePattern = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9!#$&^_+.-]{0,62}/[a-zA-Z0-9][a-zA-Z0-9!#$&^_+.-]{0,62}(; ?charset=[a-zA-Z0-9._-]{1,32})?$",
        RegexOptions.Compiled);

    public async Task<SiteHostingSettingsDto> GetAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<SiteHostingSettingsDto>(), cancellationToken)).ToList();
        var existing = states.FirstOrDefault(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // OKUMA yolunda yazma ÖLÇÜMÜ YOKTUR. Geçmiş bir "doğrulandı"yı bugüne taşımak,
            // aradan geçen sürede elle bozulmuş bir web.config'i yeşil göstermek olurdu.
            existing.WebConfigWrite = WebConfigWriteMeasurement.Olculemedi();
            return existing;
        }

        var zoneHost = site.Bindings
            .Select(binding => binding.HostName)
            .FirstOrDefault(host => !string.IsNullOrWhiteSpace(host));
        var state = new SiteHostingSettingsDto
        {
            SiteName = site.Name,
            DefaultDocuments = ["index.html", "index.php", "Default.aspx", "default.asp"],
            StaticCompressionEnabled = true,
            MaximumUploadBytes = 31_457_280,
            AllowedReferrerHosts = string.IsNullOrWhiteSpace(zoneHost) ? [] : [zoneHost],
            ProtectedExtensions = ["jpg", "jpeg", "png", "gif", "webp", "svg", "mp4"]
        };
        states.Add(state);
        await fileStore.WriteAsync(StatePath, states, cancellationToken);

        // BURADA web.config YAZILMAZ. Okuma işleminin yan etkisi olmamalı — hele ki müşterinin
        // sitesini düşürebilecek bir yan etkisi.
        //
        // Eskiden bu satırda UpdateWebConfig çağrılıyordu. UpdateWebConfig ise ipSecurity,
        // httpRedirect ve urlCompression bölümlerini KOŞULSUZ yazıyor; bu bölümler sırasıyla
        // IP and Domain Restrictions, HTTP Redirection ve sıkıştırma rol hizmetlerini gerektirir
        // ve panelin kurulum betiği (scripts/enable-iis-features.ps1) bunların HİÇBİRİNİ kurmuyor.
        // IIS tanımlanmamış bir config bölümü görünce sitenin tamamına 500.19 döner.
        //
        // Yani kullanıcı hiçbir ayar kaydetmeden, yalnızca "Barındırma ayarları" sekmesini AÇARAK
        // siteyi düşürebiliyordu. Ayarlar zaten Update akışında yazılıyor; burada yazmanın bir
        // kazancı yoktu.
        return state;
    }

    public async Task<SiteHostingSettingsDto> UpdateAsync(
        string siteName,
        UpdateSiteHostingSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        Validate(site.PhysicalPath, request);

        // YAZ, SONRA GERİ OKU — ve IIS'e de SOR. Eskiden burada dönüş değeri olmayan bir çağrı
        // vardı; panel koşulsuz "kaydedildi" diyordu. XML olarak geçerli bir belge IIS için hâlâ
        // geçersiz olabilir (tanımsız bölüm → sitenin TAMAMINA 500.19), o yüzden ikinci bir
        // kapı var. IIS yoksa cevap "reddetti" DEĞİL, "ölçülemedi"dir.
        var yazma = UpdateWebConfig(site.PhysicalPath, request, () => IisWebConfigOkuyabiliyorMu(site.Name),
            configureStream: ConfigurationStream, cancellationToken: cancellationToken);
        if (!yazma.Verified || (yazma.IisProbed && !yazma.IisAccepted))
        {
            // Diske inmeyen / IIS'in reddettiği bir yazma "kaydedildi" DEĞİLDİR; önceki içerik
            // geri konuldu (bkz. KaydetVeGeriOku) ve kullanıcıya ADIYLA söylenir.
            throw new AppException(yazma.NoticeKey, 500, 500, yazma.Detail);
        }

        try
        {
            // IIS KUYRUĞUNA GİRİYOR: applicationHost.config'e yazıyor ve tek dosyadır — eşzamanlı
            // yazımda biri sessizce kaybolur. Gerekçe WafService.IsolateAppPoolAsync'te.
            await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Iis, () =>
            {
                UpdateIisLimits(siteName, request.MaximumBandwidthBytesPerSecond, request.MaximumConnections);
                return Task.CompletedTask;
            }, cancellationToken);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapIisException(exception, "sites.write_failed");
        }

        var state = new SiteHostingSettingsDto
        {
            SiteName = site.Name,
            MaximumBandwidthBytesPerSecond = Math.Max(0, request.MaximumBandwidthBytesPerSecond),
            MaximumConnections = Math.Max(0, request.MaximumConnections),
            DeniedIpAddresses = request.DeniedIpAddresses.Select(item => item.Trim()).Where(item => item.Length > 0).Distinct().ToArray(),
            RedirectUrl = request.RedirectUrl.Trim(),
            DirectoryBrowsingEnabled = request.DirectoryBrowsingEnabled,
            DefaultDocuments = NormalizeDefaultDocuments(request.DefaultDocuments),
            DetailedErrorsEnabled = request.DetailedErrorsEnabled,
            StaticCompressionEnabled = request.StaticCompressionEnabled,
            DynamicCompressionEnabled = request.DynamicCompressionEnabled,
            MaximumUploadBytes = Math.Clamp(request.MaximumUploadBytes, 1_048_576, 4_294_967_295),
            HotlinkProtectionEnabled = request.HotlinkProtectionEnabled,
            AllowedReferrerHosts = request.AllowedReferrerHosts.Select(item => item.Trim()).Where(item => item.Length > 0).Distinct().ToArray(),
            ProtectedExtensions = request.ProtectedExtensions.Select(item => item.Trim().TrimStart('.')).Where(item => item.Length > 0).Distinct().ToArray(),
            // Kayda web.config'e YAZILAN normalleştirilmiş biçim girer; ekranın gösterdiği ile
            // sunucuda duran aynı şey olsun (ham girdi saklansa ".woff2" ve "woff2" farklı görünürdü).
            CustomErrorPages = NormalizeErrorPages(site.PhysicalPath, request.CustomErrorPages),
            MimeTypes = NormalizeMimeTypes(request.MimeTypes),
            HstsEnabled = request.HstsEnabled,
            HstsMaxAgeSeconds = request.HstsMaxAgeSeconds,
            HstsIncludeSubDomains = request.HstsIncludeSubDomains,
            HstsPreload = request.HstsPreload
        };

        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<SiteHostingSettingsDto>(), cancellationToken)).ToList();
        states.RemoveAll(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));
        states.Add(state);
        await fileStore.WriteAsync(StatePath, states, cancellationToken);

        // ÖLÇÜM KAYDEDİLDİKTEN SONRA İLİŞTİRİLİR. Durum dosyasına yazılsaydı, bir sonraki OKUMA
        // eski bir ölçümü BUGÜNÜN gerçeği gibi gösterirdi — düzeltmeye çalıştığımız hata sınıfının
        // ta kendisi.
        state.WebConfigWrite = yazma;
        return state;
    }

    public async Task<ComposerRunResultDto> RunComposerInstallAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var composer = catalog.Installed.FirstOrDefault(item =>
            item.ComponentId == "composer" && item.Status.Equals("Installed", StringComparison.OrdinalIgnoreCase));
        var php = catalog.Installed.FirstOrDefault(item =>
            item.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase) &&
            item.Status.Equals("Installed", StringComparison.OrdinalIgnoreCase));

        if (composer is null || php is null)
        {
            throw new AppException("composer.components_required", 409, 409);
        }

        var composerPath = Path.Combine(composer.InstallDir, "composer.phar");
        var phpPath = Path.Combine(php.InstallDir, "php.exe");
        if (!File.Exists(composerPath) || !File.Exists(phpPath))
        {
            throw new AppException("composer.components_required", 409, 409);
        }

        var kok = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        var result = await LimitedIdentityRunner.RunAsync(
            phpPath,
            $"\"{composerPath}\" install --no-interaction --prefer-dist --optimize-autoloader",
            kok,
            kok,
            TimeSpan.FromMinutes(10),
            cancellationToken);

        return new ComposerRunResultDto
        {
            ExitCode = result.ExitCode,
            Output = result.StandardOutput,
            Error = result.StandardError
        };
    }

    public async Task<ComposerRunResultDto> RunComposerCommandAsync(string siteName, string command, CancellationToken cancellationToken = default)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 300)
        {
            throw new AppException("composer.invalid_command", 400, 400);
        }

        // Shell metakarakteri reddedilir (ProcessRunner shell KULLANMAZ ama defense-in-depth). Komut, composer'a
        // ARGÜMAN olarak geçer; tam shell enjeksiyonu yok. composer.json script'leri sitenin kendi koduidur (güvenilir).
        if (trimmed.IndexOfAny(new[] { '&', '|', ';', '<', '>', '`', '\n', '\r', '"', '%', '^' }) >= 0)
        {
            throw new AppException("composer.invalid_command", 400, 400);
        }

        // Kullanıcı başına "composer" yazdıysa at (composer.phar'ı zaten biz çağırıyoruz).
        if (trimmed.StartsWith("composer ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["composer ".Length..].Trim();
        }

        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var composer = catalog.Installed.FirstOrDefault(item =>
            item.ComponentId == "composer" && item.Status.Equals("Installed", StringComparison.OrdinalIgnoreCase));
        var php = catalog.Installed.FirstOrDefault(item =>
            item.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase) &&
            item.Status.Equals("Installed", StringComparison.OrdinalIgnoreCase));

        if (composer is null || php is null)
        {
            throw new AppException("composer.components_required", 409, 409);
        }

        var composerPath = Path.Combine(composer.InstallDir, "composer.phar");
        var phpPath = Path.Combine(php.InstallDir, "php.exe");
        if (!File.Exists(composerPath) || !File.Exists(phpPath))
        {
            throw new AppException("composer.components_required", 409, 409);
        }

        var kok = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        var result = await LimitedIdentityRunner.RunAsync(
            phpPath,
            $"\"{composerPath}\" {trimmed} --no-interaction --no-ansi",
            kok,
            kok,
            TimeSpan.FromMinutes(10),
            cancellationToken);

        return new ComposerRunResultDto
        {
            ExitCode = result.ExitCode,
            Output = result.StandardOutput,
            Error = result.StandardError
        };
    }

    public async Task<ComposerRunResultDto> RunArtisanCommandAsync(string siteName, string command, CancellationToken cancellationToken = default)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 300)
        {
            throw new AppException("artisan.invalid_command", 400, 400);
        }

        if (trimmed.IndexOfAny(new[] { '&', '|', ';', '<', '>', '`', '\n', '\r', '"', '%', '^' }) >= 0)
        {
            throw new AppException("artisan.invalid_command", 400, 400);
        }

        // Kullanıcı başına "php artisan" / "artisan" yazdıysa at (artisan'ı zaten biz çağırıyoruz).
        if (trimmed.StartsWith("php artisan ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["php artisan ".Length..].Trim();
        }
        else if (trimmed.StartsWith("artisan ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["artisan ".Length..].Trim();
        }

        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var workDir = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        var artisanPath = Path.Combine(workDir, "artisan");
        if (!File.Exists(artisanPath))
        {
            // artisan dosyası yok → Laravel uygulaması değil.
            throw new AppException("artisan.not_found", 409, 409);
        }

        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var php = catalog.Installed.FirstOrDefault(item =>
            item.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase) &&
            item.Status.Equals("Installed", StringComparison.OrdinalIgnoreCase));
        if (php is null)
        {
            throw new AppException("composer.components_required", 409, 409);
        }

        var phpPath = Path.Combine(php.InstallDir, "php.exe");
        if (!File.Exists(phpPath))
        {
            throw new AppException("composer.components_required", 409, 409);
        }

        var result = await LimitedIdentityRunner.RunAsync(
            phpPath,
            $"\"{artisanPath}\" {trimmed} --no-interaction --no-ansi",
            workDir,
            workDir,
            TimeSpan.FromMinutes(10),
            cancellationToken);

        return new ComposerRunResultDto
        {
            ExitCode = result.ExitCode,
            Output = result.StandardOutput,
            Error = result.StandardError
        };
    }

    public async Task<ComposerRunResultDto> RunNodeCommandAsync(string siteName, string command, CancellationToken cancellationToken = default)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 300)
        {
            throw new AppException("node.invalid_command", 400, 400);
        }

        if (trimmed.IndexOfAny(new[] { '&', '|', ';', '<', '>', '`', '\n', '\r', '"', '%', '^' }) >= 0)
        {
            throw new AppException("node.invalid_command", 400, 400);
        }

        if (trimmed.StartsWith("npm ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["npm ".Length..].Trim();
        }

        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var workDir = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        var nodeExe = await ResolveNodeExecutableAsync(cancellationToken);
        // npm.cmd batch'i yerine node.exe + npm-cli.js (UseShellExecute=false ile .cmd sorunlarından kaçınır).
        var npmCli = Path.Combine(Path.GetDirectoryName(nodeExe)!, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npmCli))
        {
            throw new AppException("node.not_installed", 409, 409);
        }

        var result = await LimitedIdentityRunner.RunAsync(
            nodeExe,
            $"\"{npmCli}\" {trimmed} --no-fund --no-audit",
            workDir,
            workDir,
            TimeSpan.FromMinutes(20),
            cancellationToken);

        return new ComposerRunResultDto
        {
            ExitCode = result.ExitCode,
            Output = result.StandardOutput,
            Error = result.StandardError
        };
    }

    public async Task<QueueWorkerStatusDto> GetQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var taskName = QueueTaskName(siteName);
        var tasks = await schedulerService.GetTasksAsync(cancellationToken);
        var running = tasks.Any(task => string.Equals(task.Name, taskName, StringComparison.OrdinalIgnoreCase));
        return new QueueWorkerStatusDto { Running = running, TaskName = taskName };
    }

    public async Task<QueueWorkerStatusDto> StartQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var workDir = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        if (!File.Exists(Path.Combine(workDir, "artisan")))
        {
            throw new AppException("artisan.not_found", 409, 409);
        }

        var phpPath = await ResolvePhpExecutableAsync(cancellationToken);

        // panel Kuyruk gibi: minutely zamanlanmış görev + --max-time = kendini yenileyen kalıcı worker.
        // Windows Görev Zamanlayıcı varsayılanı "çalışıyorsa yeni örnek başlatma" olduğundan tek worker döner;
        // --max-time ile saatte bir taze süreç (Laravel'in bellek sızıntısına karşı önerisi).
        // SINIRLI KİMLİK — ÖLÇÜLEN AÇIK: bu uç [Authorize(Roles = "Administrator,Reseller")]
        // altında, yani BİR KİRACI (bayi) tetikleyebiliyor ve görev sitenin KENDİ Laravel kodunu
        // çalıştırıyor. SYSTEM olarak kaydedildiğinde "kuyruk işçisini başlat" düğmesi, kiracı
        // için SYSTEM'de keyfi kod çalıştırma düğmesiydi — panelin kendi kuralına da aykırı:
        // CronController "müşteri/bayi komut/PHP/betik görevi kuramaz" diyor.
        //
        // SiteName VERİLİYOR: sınırlı kimlik site kökünü okuyup yazabilsin (LimitedIdentityRunner
        // aynı komutlar için aynı izni zaten veriyor); verilmezse görev her turda düşerdi.
        await schedulerService.CreateTaskAsync(new CreateScheduledTaskRequest
        {
            Name = QueueTaskName(siteName),
            Command = phpPath,
            Arguments = "artisan queue:work --tries=3 --sleep=3 --max-time=3600",
            WorkingDirectory = workDir,
            SiteName = siteName,
            ScheduleType = "minutely",
            Interval = 1
        }, ScheduledTaskPrivilege.Limited, cancellationToken);

        // BASLATMA IDDIASI DA OLCULUR. `Running = true` de tipki `Running = false` gibi hicbir
        // olcume dayanmiyordu; ayni geri okuma iki yonde de kullanilir (TEK KAYNAK).
        return await DurumuOlcAsync(QueueTaskName(siteName), null, cancellationToken);
    }

    public async Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        // ÖNCE ARKA PLAN GÖREVLERİ, sonra ayar kaydı.
        //
        // Kuyruk işçisi ve Laravel zamanlayıcı birer Windows zamanlanmış görevidir; site
        // gittikten sonra da tetiklenmeye devam ederler. Çalıştıracakları dosya artık yoktur:
        // her turda hata üretir, günlüğü kirletir ve panelde "başarısız görev" olarak görünürler.
        // Kullanıcı da silinmiş bir siteye ait bu görevleri panelde göremediği için bağ kuramaz.
        //
        // Her ikisi de best-effort: görev zaten yoksa DeleteTaskAsync sessizce geçer (aşağıdaki
        // metotlar kendi try/catch'leriyle sarılı) ve silme akışı durmaz.
        await StopQueueWorkerAsync(siteName, cancellationToken);
        await StopLaravelSchedulerAsync(siteName, cancellationToken);

        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<SiteHostingSettingsDto>(), cancellationToken)).ToList();
        var kaldirilan = states.RemoveAll(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));

        if (kaldirilan > 0)
        {
            await fileStore.WriteAsync(StatePath, states, cancellationToken);
        }
    }

    public async Task<QueueWorkerStatusDto> StopQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var taskName = QueueTaskName(siteName);
        var silmeHatasi = await GoreviSilAsync(taskName, cancellationToken);

        // İşçilere işi bitirince nazikçe çık sinyali (best-effort); zaten --max-time ile de durur.
        try
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            var workDir = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
            if (File.Exists(Path.Combine(workDir, "artisan")))
            {
                var phpPath = await ResolvePhpExecutableAsync(cancellationToken);
                await ProcessRunner.RunAsync(phpPath, "artisan queue:restart", workDir, cancellationToken, timeout: TimeSpan.FromSeconds(30));
            }
        }
        catch
        {
            // Sinyal gönderilemese de worker yine duracak.
        }

        return await DurumuOlcAsync(taskName, silmeHatasi, cancellationToken);
    }

    /// <summary>
    /// Görevi siler; sonucu FIRLATMADAN döndürür (silinemediyse hatanın kendisi döner).
    /// </summary>
    /// <remarks>
    /// Eskiden buradaki <c>catch (AppException)</c> "görev zaten yoksa sorun değil" diyerek
    /// HER silme hatasını yutuyordu — silme GERÇEKTEN başarısız olduğunda da. Ayrım artık
    /// aşağıdaki GERİ OKUMA ile yapılıyor: görev hâlâ listede mi?
    /// </remarks>
    private async Task<AppException?> GoreviSilAsync(string taskName, CancellationToken cancellationToken)
    {
        try
        {
            await schedulerService.DeleteTaskAsync(taskName, cancellationToken);
            return null;
        }
        catch (AppException exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// "Durduruldu" iddiasını ÖLÇER: görev listesini geri okur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN HATA.</b> Bu iki durdurma yolu <c>new QueueWorkerStatusDto { Running = false }</c>
    /// dönüyordu — hiçbir ölçüme dayanmayan bir iddia. Silme başarısız olsa bile aynı satır
    /// çalışıyordu; işçi koşmaya devam ederken panel "durduruldu" diyordu.
    /// </para>
    /// <para>
    /// <b>ÜÇ DEĞER.</b> Görev listesi okunamazsa cevap "duruyor" DEĞİL, ÖLÇÜLEMEDİ'dir
    /// (<see cref="QueueWorkerStatusDto.Measured"/> false). Okunabiliyorsa
    /// <see cref="QueueWorkerStatusDto.Running"/> görevin listede olup olmamasıdır — iddia değil,
    /// gözlem.
    /// </para>
    /// </remarks>
    private async Task<QueueWorkerStatusDto> DurumuOlcAsync(
        string taskName,
        AppException? silmeHatasi,
        CancellationToken cancellationToken)
    {
        try
        {
            var gorevler = await schedulerService.GetTasksAsync(cancellationToken);
            var duruyor = gorevler.Any(item => string.Equals(item.Name, taskName, StringComparison.OrdinalIgnoreCase));

            return new QueueWorkerStatusDto
            {
                TaskName = taskName,
                Running = duruyor,
                Measured = true,
                Detail = duruyor
                    ? $"Gorev HALA kayitli: {taskName}."
                      + (silmeHatasi is null ? string.Empty : $" Silme hatasi: {silmeHatasi.MessageKey}.")
                    : $"Gorev kayitli degil: {taskName}."
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Gorev listesi okunamadi. "Durduruldu" DEMEYIZ.
            return new QueueWorkerStatusDto
            {
                TaskName = taskName,
                Running = false,
                Measured = false,
                Detail = "Gorev listesi OKUNAMADI: durum olculemedi. " + exception.Message
            };
        }
    }

    private async Task<string> ResolvePhpExecutableAsync(CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var php = catalog.Installed.FirstOrDefault(item =>
            item.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase) &&
            item.Status.Equals("Installed", StringComparison.OrdinalIgnoreCase));
        if (php is null)
        {
            throw new AppException("composer.components_required", 409, 409);
        }

        var phpPath = Path.Combine(php.InstallDir, "php.exe");
        if (!File.Exists(phpPath))
        {
            throw new AppException("composer.components_required", 409, 409);
        }

        return phpPath;
    }

    private async Task<string> ResolveNodeExecutableAsync(CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var node = catalog.Installed.FirstOrDefault(item =>
            item.ComponentId.StartsWith("nodejs", StringComparison.OrdinalIgnoreCase) &&
            item.Status.Equals("Installed", StringComparison.OrdinalIgnoreCase));

        var candidates = new List<string>();
        if (node is not null)
        {
            candidates.Add(Path.Combine(node.InstallDir, "node.exe"));
        }
        candidates.Add(Environment.ExpandEnvironmentVariables("%ProgramFiles%\\nodejs\\node.exe"));
        candidates.Add(Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%\\nodejs\\node.exe"));

        var nodeExe = candidates.FirstOrDefault(File.Exists);
        if (nodeExe is null)
        {
            throw new AppException("node.not_installed", 409, 409);
        }

        return nodeExe;
    }

    private static string QueueTaskName(string siteName)
    {
        var safe = new string(siteName
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .ToArray());
        return $"AdaQueue-{safe}";
    }

    public async Task<QueueWorkerStatusDto> GetLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var taskName = SchedulerTaskName(siteName);
        var tasks = await schedulerService.GetTasksAsync(cancellationToken);
        var running = tasks.Any(task => string.Equals(task.Name, taskName, StringComparison.OrdinalIgnoreCase));
        return new QueueWorkerStatusDto { Running = running, TaskName = taskName };
    }

    public async Task<QueueWorkerStatusDto> StartLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var workDir = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        if (!File.Exists(Path.Combine(workDir, "artisan")))
        {
            throw new AppException("artisan.not_found", 409, 409);
        }

        var phpPath = await ResolvePhpExecutableAsync(cancellationToken);

        // panel Laravel Toolkit "Scheduler": dakikada bir `php artisan schedule:run` (Laravel'in tek cron girdisi).
        // SINIRLI KİMLİK — kuyruk işçisiyle AYNI gerekçe (yukarıda ölçüldü): uç bayiye açık ve
        // görev sitenin kendi kodunu çalıştırıyor.
        await schedulerService.CreateTaskAsync(new CreateScheduledTaskRequest
        {
            Name = SchedulerTaskName(siteName),
            Command = phpPath,
            Arguments = "artisan schedule:run",
            WorkingDirectory = workDir,
            SiteName = siteName,
            ScheduleType = "minutely",
            Interval = 1
        }, ScheduledTaskPrivilege.Limited, cancellationToken);

        return await DurumuOlcAsync(SchedulerTaskName(siteName), null, cancellationToken);
    }

    public async Task<QueueWorkerStatusDto> StopLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var taskName = SchedulerTaskName(siteName);
        var silmeHatasi = await GoreviSilAsync(taskName, cancellationToken);

        return await DurumuOlcAsync(taskName, silmeHatasi, cancellationToken);
    }

    // AppStoreService Laravel kurulumuyla AYNI ad → panel-kurulu Laravel'de oluşan görevi de yönetir, mükerrer olmaz.
    private static string SchedulerTaskName(string siteName)
    {
        var safe = new string(siteName
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .ToArray());
        return $"Laravel-{safe}-Scheduler";
    }

    public async Task<ComposerRunResultDto> RunWpCliCommandAsync(string siteName, string command, CancellationToken cancellationToken = default)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 300)
        {
            throw new AppException("wpcli.invalid_command", 400, 400);
        }

        if (trimmed.IndexOfAny(new[] { '&', '|', ';', '<', '>', '`', '\n', '\r', '"', '%', '^' }) >= 0)
        {
            throw new AppException("wpcli.invalid_command", 400, 400);
        }

        if (trimmed.StartsWith("wp ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["wp ".Length..].Trim();
        }

        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var workDir = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        if (!File.Exists(Path.Combine(workDir, "wp-load.php")) && !File.Exists(Path.Combine(workDir, "wp-config.php")))
        {
            throw new AppException("wpcli.not_wordpress", 409, 409);
        }

        var phpPath = await ResolvePhpExecutableAsync(cancellationToken);
        var wpCli = await EnsureWpCliAsync(cancellationToken);

        var result = await LimitedIdentityRunner.RunAsync(
            phpPath,
            $"\"{wpCli}\" {trimmed} --no-color",
            workDir,
            workDir,
            TimeSpan.FromMinutes(10),
            cancellationToken);

        return new ComposerRunResultDto
        {
            ExitCode = result.ExitCode,
            Output = result.StandardOutput,
            Error = result.StandardError
        };
    }

    // wp-cli.phar yoksa ilk kullanımda resmi build'den indirir (panel WP Toolkit'in altında da wp-cli vardır).
    private async Task<string> EnsureWpCliAsync(CancellationToken cancellationToken)
    {
        var toolsDir = Path.Combine(dataRoot, "tools");
        var wpCli = Path.Combine(toolsDir, "wp-cli.phar");
        if (File.Exists(wpCli))
        {
            return wpCli;
        }

        Directory.CreateDirectory(toolsDir);
        var dest = wpCli.Replace("'", "''");
        var script =
            "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; " +
            $"Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/wp-cli/builds/gh-pages/phar/wp-cli.phar' -OutFile '{dest}' -UseBasicParsing";
        var download = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!download.Success || !File.Exists(wpCli))
        {
            throw new AppException("wpcli.unavailable", 503, 503, download.StandardError);
        }

        return wpCli;
    }

    /// <summary>
    /// Arşiv adresi panelin ULAŞMAMASI gereken bir yere mi bakıyor; bakıyorsa NEREYE (günlük/ayrıntı
    /// için). Temizse <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aralık listesi <see cref="OutboundAddressGuard"/>'dan gelir; burada yalnızca bu yola ÖZGÜ üç
    /// karar var:
    /// </para>
    /// <list type="number">
    /// <item><b>Kullanıcı bilgisi reddedilir.</b> <c>http://gercek-sunucu.example@169.254.169.254/x.zip</c>
    /// adresinde insan gözü ilk parçayı, ayrıştırıcı ikincisini okur; ayrıca bu alanda kimliğe hiç
    /// gerek yok (kimlik gereken kaynak için panelin ayrı FTP/SFTP göç yolu var).</item>
    /// <item><b>DNS ÇÖZÜLEMEZSE REDDEDİLİR</b> — ve bu, müşteri cron adresindeki kuraldan bilerek
    /// farklıdır. Orada "çözülemedi = geç" demek meşru bir siteyi geçici bir DNS hatası yüzünden
    /// kalıcı olarak engellememek içindi. Burada aynı gerekçe YOK: çözülemeyen bir addan indirme
    /// zaten yapılamaz (indirmeyi çalıştıran <c>Invoke-WebRequest</c> de aynı çözümleyiciyi
    /// kullanır), yani reddetmek hiçbir çalışan senaryoyu kapatmıyor.</item>
    /// <item><b>Tüm çözülen adresler denetlenir</b>, ilki değil: bir ad hem genel hem iç adrese
    /// çözülüyorsa hangisine bağlanılacağı bize bağlı değildir.</item>
    /// </list>
    /// <para>
    /// <b>KAPSAM — bu kapı YÖNLENDİRMEYİ görmez, ama artık tek katman değil.</b> Burası yalnız
    /// "istek atılmadan önce ad nereye çözülüyor" sorusunu ölçer ve kullanıcıya soket açılmadan,
    /// anlaşılır bir hata verir. <c>http/https</c> indirmesi artık panelin DENETLENEN
    /// <c>HttpClient</c>'ından geçiyor (<see cref="ImportSiteArchiveAsync"/>), yani yönlendirmenin
    /// HER ADIMI ve DNS rebinding <see cref="OutboundConnectGuard"/> tarafından bağlanma anında
    /// denetleniyor. <c>ftp://</c> hâlâ <c>Invoke-WebRequest</c> ile iner (gerekçe ve kalan sınır
    /// <see cref="ImportSiteArchiveAsync"/>'te yazılı).
    /// </para>
    /// </remarks>
    /// <param name="policy">
    /// <b>ZORUNLU ve VARSAYILANSIZ — sabit <c>Strict</c> BİR HATAYDI.</b> Bu ön kapı bir süre
    /// politikadan bağımsız <c>Strict</c> kullandı ve bu, ölçülünce yanlış çıktı: asıl sınır olan
    /// bağlanma-anı kapısı (<c>OutboundConnectGuard</c>, <c>ServiceCollectionExtensions</c>'ta
    /// kurulur) <c>Connecting(AllowInternalOutboundTargets)</c> kullanıyor ve oradaki yorum, açık
    /// kapının orada GEÇERLİ OLMASI gerektiğini açıkça yazıyor ("yoksa iç ağdaki NAS'a yedek atan
    /// meşru kurulum kapıyı açtığı hâlde soket düzeyinde reddedilirdi").
    /// <para>
    /// <b>Kural: ön kapı asıl sınırdan DAHA SIKI olamaz.</b> Sıkı olursa işleticinin bilerek açtığı
    /// kapıyı sessizce hükümsüz kılar — üstelik gerçek sınır hâlâ oradadır, yani kazanılan güvenlik
    /// YOKTUR; kazanılan tek şey kullanıcının anlayamayacağı bir 403'tür. Parametre varsayılansız,
    /// çünkü bu kararı çağrı yeri VERMELİ; "unutulunca en sıkısı" davranışı tam olarak buradaki
    /// hatayı üretmişti.
    /// </para>
    /// </param>
    internal static async Task<string?> FindBlockedImportTargetAsync(
        string url,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver,
        OutboundTargetPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri))
        {
            return "invalid_uri";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "userinfo";
        }

        // GÖVDE ARTIK BURADA DEĞİL (OutboundAddressGuard.FindBlockedHostAsync); yalnız bu yola ÖZGÜ
        // kararlar (kullanıcı bilgisi reddi, yukarıda) burada kaldı. "Çözülemedi = reddet"
        // politikası korunuyor — gerekçesi yukarıdaki listenin 2. maddesinde yazılı.
        //
        // DnsSafeHost: IPv6 literalinde köşeli parantezleri atar, IDN adı punycode'a çevirir ve
        // ondalık/sekizlik/onaltılık IPv4 yazımlarını noktalı dörtlüye normalleştirir.
        return await OutboundAddressGuard.FindBlockedHostAsync(
            uri.DnsSafeHost,
            (ad, iptal) => resolver(ad, iptal),
            policy,
            cancellationToken);
    }

    /// <remarks>
    /// Tam nitelikli: bu derlemede <c>AdaPanel.Infrastructure.Dns</c> adında bir ad alanı var ve
    /// kısa <c>Dns</c> yazımı ona bağlanıp derlemeyi kırıyor (aynı tuzak ClusterAddressGuard'da da
    /// yaşandı).
    /// </remarks>
    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>
    /// Arşivi panelin DENETLENEN <c>HttpClient</c>'ıyla indirir; yönlendirmenin her adımı bağlanma
    /// anında denetlenir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden akışa (stream) yazılıyor:</b> arşivin boyutunu saldırgan belirler; yanıtı önce
    /// belleğe almak (<c>ReadAsByteArrayAsync</c>) tek bir istekle süreci şişirirdi. Diskteki ağaç
    /// için üst sınır zaten var (<see cref="IceAktarmaOgeButcesi"/>); burada eklenen şey belleğin
    /// hiç devreye girmemesidir.
    /// </para>
    /// <para>
    /// <b>İstemci fabrikası yoksa indirme YAPILMAZ.</b> Eski denetimsiz yola "geri düşmek" en
    /// tehlikeli tasarım olurdu: koruma, bir bağımlılığın kaydedilmemiş olmasıyla sessizce
    /// kalkardı. Üretimde fabrika her zaman kayıtlıdır (<c>AddHttpClient</c>).
    /// </para>
    /// </remarks>
    /// <summary>
    /// İçe aktarmanın PowerShell adımını kurar: <c>ftp</c> için indirme + açma, <c>http/https</c>
    /// için YALNIZ açma.
    /// </summary>
    /// <remarks>
    /// <b>Neden ayrı ve internal:</b> "http/https artık PowerShell'den İNMİYOR" iddiası ancak
    /// kurulan komut satırı okunarak ölçülebilir; kaynak taraması değil, üretimde çalıştırılan
    /// dizenin ta kendisi sınanıyor. Bu ayrım kalkarsa (yani http yine <c>Invoke-WebRequest</c>'e
    /// düşerse) yönlendirme denetimi de sessizce kalkmış olurdu.
    /// </remarks>
    internal static string BuildImportScript(bool ftp, string url, string archive, string extractDir)
        => (ftp
                ? "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; " +
                  $"Invoke-WebRequest -Uri '{url}' -OutFile '{archive.Replace("'", "''")}' -UseBasicParsing; "
                : string.Empty) +
           $"Expand-Archive -Path '{archive.Replace("'", "''")}' -DestinationPath '{extractDir.Replace("'", "''")}' -Force";

    private async Task DenetlenenIndirmeAsync(string url, string hedefDosya, CancellationToken cancellationToken)
    {
        if (httpClientFactory is null)
        {
            throw new AppException("migration.download_failed", 502, 502, "guarded http client unavailable");
        }

        var istemci = httpClientFactory.CreateClient(OutboundConnectGuard.GuardedClientName);

        // Büyük site arşivleri uzun sürer; HttpClient'ın 100 sn'lik varsayılanı meşru bir göçü
        // yarıda keserdi. Üst sınır PowerShell yolundaki süreç tavanıyla (10 dk) aynı düzende.
        istemci.Timeout = TimeSpan.FromMinutes(30);

        try
        {
            using var yanit = await istemci.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!yanit.IsSuccessStatusCode)
            {
                throw new AppException(
                    "migration.download_failed",
                    502,
                    502,
                    $"HTTP {(int)yanit.StatusCode} {yanit.ReasonPhrase}".Trim());
            }

            await using var dosya = new FileStream(hedefDosya, FileMode.Create, FileAccess.Write, FileShare.None);
            await yanit.Content.CopyToAsync(dosya, cancellationToken);
        }
        catch (Exception exception) when (OutboundBlockedException.Cikar(exception) is { } engellenen)
        {
            // .NET, ConnectCallback'ten atılan istisnayı KENDİ HttpRequestException'ının içine
            // sarar; zinciri taramadan yakalayan bir catch HİÇ tetiklenmez (gerekçe
            // OutboundBlockedException.Cikar'da yazılı). Kullanıcı "ağ hatası" değil ADRES YASAĞI
            // görür — ki bu yasağın tek görünür yüzü odur.
            throw new AppException("migration.url_forbidden", 403, 403, engellenen.BlockedTarget);
        }
        catch (Exception exception) when (exception is not AppException && exception is not OperationCanceledException)
        {
            throw new AppException("migration.download_failed", 502, 502, TruncateMigrationError(exception.Message));
        }
    }

    public async Task<SiteImportResultDto> ImportSiteArchiveAsync(string siteName, string archiveUrl, bool cleanFirst, CancellationToken cancellationToken = default)
    {
        var url = (archiveUrl ?? string.Empty).Trim();
        if (url.Length == 0 || url.Length > 2000 ||
            url.IndexOfAny(new[] { '"', '\'', '`', '\n', '\r', '|', '&', ';', '<', '>', '^' }) >= 0)
        {
            throw new AppException("migration.invalid_url", 400, 400);
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("migration.invalid_url", 400, 400);
        }

        // SSRF KAPISI. Bu uç Bayi (Reseller) rolüne açıktır ve indirmeyi PANEL yapar: istek
        // sunucunun kendi ağından, LocalSystem hesabıyla çıkar. Denetimsiz bırakıldığında bayi,
        // "arşiv adresi" alanına yazarak panelin ağ konumunu ödünç alıyordu —
        // `http://169.254.169.254/...` (bulut kimlik bilgileri), `http://127.0.0.1:<port>/`
        // (yalnız loopback'te dinleyen yönetim uçları), `http://10.0.0.5/` (iç ağ).
        // POLİTİKA, asıl sınırın kullandığının AYNISI olmak zorunda (bkz. parametrenin belgesi):
        // bağlanma-anı kapısı `Connecting(AllowInternalOutboundTargets)` kullanıyor. Burada sabit
        // `Strict` yazmak, işleticinin bilerek açtığı kapıyı sessizce hükümsüz kılardı — ve gerçek
        // sınır zaten yerinde olduğu için karşılığında hiçbir güvenlik kazandırmazdı.
        var engellenen = await FindBlockedImportTargetAsync(
            url,
            outboundResolver ?? ResolveHostAddressesAsync,
            OutboundTargetPolicy.Connecting(runtimeOptions.Value.AllowInternalOutboundTargets),
            cancellationToken);
        if (engellenen is not null)
        {
            throw new AppException("migration.url_forbidden", 403, 403, engellenen);
        }

        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        Directory.CreateDirectory(root);

        var work = Path.Combine(dataRoot, "temp", "migrate", Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(work, "extract");
        var archive = Path.Combine(work, "site-archive.zip");
        Directory.CreateDirectory(extractDir);

        try
        {
            // İNDİRME ARTIK PANELİN DENETLENEN İSTEMCİSİNDEN GEÇİYOR (http/https).
            //
            // ESKİ HÂLİ ÖLÇÜLEBİLİR BİÇİMDE AÇIKTI: indirmeyi `Invoke-WebRequest` yapıyordu ve o,
            // 3xx yanıtlarını KENDİ izler. Yukarıdaki kapı yalnız İLK adresi görüyordu; dış bir
            // sunucunun `Location: http://169.254.169.254/` demesi denetimden SONRA gerçekleşiyordu.
            // Aynı boşluk DNS rebinding'i de kapsıyordu (denetlenen ad ile bağlanılan adres iki ayrı
            // çözümlemeydi). `OutboundConnectGuard.ConnectCallback` HER BAĞLANTIDA çalışır — yani her
            // yönlendirme adımında — ve adı BİR KEZ çözüp soketi tam o adreslere açar. Kalıp
            // efade94'te cron adresi için kurulan kalıbın aynısıdır; yeni bir kural YAZILMADI.
            //
            // YÖNLENDİRME KAPATILMADI: meşru arşiv indirmeleri (CDN, S3 bölge yönlendirmesi,
            // http→https) zinciri sonuna kadar izler ve çalışmaya devam eder.
            //
            // `ftp://` HÂLÂ PowerShell ile iner ve bu BİLİNÇLİ. Ölçüldü: Windows PowerShell 5.1'de
            // `Invoke-WebRequest ftp://…` gerçekten çalışır (`FtpWebRequest` üretir ve bağlanır —
            // bu makinede 530 yanıtı alındı), yani ftp desteği `Invoke-WebRequest`'in yan etkisi
            // değil; adres doğrulaması `ftp://` şemasını AÇIKÇA beyaz listeye alıyor ve arayüz onu
            // kullanıcıya VAAT EDİYOR (`panel.migration.urlHint`: "http/https/ftp ile erişilebilen
            // bir .zip"). İndirmeyi tümden `HttpClient`'a taşımak bu vaadi sessizce kırardı.
            // KALAN SINIR AÇIKÇA YAZILIYOR: ftp yolunda yalnız ön kapı (yukarıdaki çözümleme +
            // kullanıcı bilgisi reddi) vardır. FTP'de yönlendirme diye bir mekanizma YOKTUR, yani
            // asıl kapatılan açık orada zaten yoktur; kalan artık risk DNS rebinding ve kötü bir
            // FTP sunucusunun PASV yanıtıyla veri kanalını başka bir adrese göstermesidir.
            var ftp = url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
            if (!ftp)
            {
                await DenetlenenIndirmeAsync(url, archive, cancellationToken);
            }

            var script = BuildImportScript(ftp, url, archive, extractDir);

            // KATI çalıştırma: bu script ÇOK İFADELİDİR ve normal PowerShell çıkış kodu yalnızca SON
            // ifadeyi yansıtır. Bozuk zip üzerinde Expand-Archive sonlandırıcı OLMAYAN hata verir →
            // powershell.exe 0 ile çıkar (bu makinede ölçüldü). Kati sürüm hatayı çıkış koduna taşır.
            var result = await ProcessRunner.RunPowerShellStrictAsync(script, cancellationToken);

            // ÇIKIŞ KODU TEK BAŞINA YETMEZ; SONUÇ ÖLÇÜLÜR.
            //
            // Eski ölçüt `result.Success && Directory.Exists(extractDir)` idi ve İKİNCİ KOŞUL HER
            // ZAMAN DOĞRUYDU: extractDir'i birkaç satır yukarıda BU KOD kendisi oluşturuyor. Yani
            // URL bir HTML hata sayfası ya da yarım dosya döndürdüğünde akış devam ediyor, sitenin
            // TÜM kökü yedeğe taşınıyor, 0 dosya kopyalanıyor ve panel "İçe aktarma tamamlandı"
            // diyordu — site bomboş kalıyordu.
            //
            // Doğru kalıp (PhpService'te zaten var): BEKLENEN ÇIKTININ gerçekten üretildiğini
            // ayrıca doğrula. Burada beklenen çıktı = arşivden çıkan EN AZ BİR DOSYA.
            var source = ResolveArchiveContentRoot(extractDir);
            var olcum = source is null ? new SafeDirectoryWalkResult(0, 0, 0, false, string.Empty) : MeasureImportTree(source);
            var availableFiles = olcum.FileCount;

            // ÇOK BÜYÜK ARŞİV: SİTEYE DOKUNMADAN reddedilir.
            //
            // KARAR (ve neden yedekten FARKLI): buradaki iş bir OKUMA değil, sitenin kökünü
            // DEĞİŞTİREN bir yazmadır. Yedekte kesilmiş bir tarama "eksik ama damgalı bir arşiv"
            // üretir — elde bir şey kalır. Burada kesilmiş bir kopyalama "yarısı yeni, yarısı yok"
            // bir site üretirdi ve o, kullanıcı için veri kaybından ayırt edilemez. Bu yüzden
            // kısmi sonuca İZİN VERİLMEZ; işlem hiç başlamaz.
            //
            // Denetim, yıkıcı adımdan (ContentSideline.MoveAside) ÖNCE olmak ZORUNDA: sonra
            // yapılsaydı sitenin eski içeriği çoktan yana alınmış olurdu.
            if (olcum.Truncated)
            {
                // ANAHTAR YÜRÜYÜŞÜN KENDİ SEBEP ANAHTARIDIR ("files.size_truncated_*") ve yeni bir
                // anahtar UYDURULMADI: çevirisi olmayan bir anahtar, kullanıcının kırmızı kutuda
                // ham anahtarı görmesi demektir (bkz. MessageKeyCoverageTests). Bu anahtarların
                // çevirisi hem tr hem en'de VAR ve sebebi doğru anlatır; bu yola ÖZGÜ olan —
                // ölçülen büyüklük ve "siteye dokunulmadı" — ayrıntı alanında durur ve arayüz onu
                // hata kutusunda anahtarın altında gösterir.
                //
                // AYRINTI CÜMLE DEĞİL ETİKET-DEĞER, çünkü bu depoda `details` ÇEVRİLMEZ: hata
                // kutusuna `useNotice.resolveApiMessage` ile "çeviri + \n\n + ham ayrıntı" olarak
                // basılır, `t()`'ye HİÇ uğramaz. Buraya Türkçe bir cümle yazmak, İngilizce panelde
                // İngilizce başlığın altında Türkçe bir satır demekti. Yeni bir i18n anahtarı da
                // çözüm değil (çevirisi olmayan anahtar EKRANDA HAM görünür, MessageKeyCoverageTests
                // bunu kapatıyor); doğru olan alanı sözleşmesine çekmek. Cümlenin taşıdığı olgular
                // aynen duruyor: ölçülen alt sınırlar, bütçe ve "siteye dokunulmadı".
                throw new AppException(
                    olcum.TruncatedReasonKey,
                    413,
                    413,
                    CokBuyukArsivAyrintisi(olcum, IceAktarmaOgeButcesi));
            }

            if (!result.Success || availableFiles == 0)
            {
                var detay = result.Success
                    ? $"Arsiv indirildi ama icinden hicbir dosya cikmadi (URL bir HTML hata sayfasi ya da yarim/bozuk zip olabilir). Indirilen boyut: {SafeFileLength(archive)} bayt."
                    : TruncateMigrationError(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);

                // HİÇBİR ŞEYE DOKUNULMADAN çıkılır: doğrulama, yıkıcı adımdan ÖNCE yapılır.
                throw new AppException("migration.download_failed", 502, 502, detay);
            }

            return ApplyImportedContent(site.Name, source!, root, cleanFirst);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, true);
                }
            }
            catch
            {
                // En iyi çaba: geçici klasör silinemezse göçü başarısız sayma.
            }
        }
    }

    /// <summary>
    /// Arşivden çıkan içeriğin gerçek kökünü bulur. Arşivde tek bir üst klasör varsa onun içi kök
    /// kabul edilir (yaygın "site/..." paketlemesi). İçerik HİÇ yoksa <c>null</c> döner — çağıran
    /// bunu "arşiv boş/bozuk" olarak okur.
    /// </summary>
    internal static string? ResolveArchiveContentRoot(string extractDirectory)
    {
        if (!Directory.Exists(extractDirectory))
        {
            return null;
        }

        var current = extractDirectory;

        // İç içe tek klasörleri (site/httpdocs/... gibi) en çok 5 kademe soyar; sonsuz döngü yok.
        for (var derinlik = 0; derinlik < 5; derinlik++)
        {
            var topLevel = new DirectoryInfo(current).GetFileSystemInfos();
            if (topLevel.Length == 1 && topLevel[0] is DirectoryInfo onlyDirectory)
            {
                current = onlyDirectory.FullName;
                continue;
            }

            return topLevel.Length == 0 ? null : current;
        }

        return current;
    }

    /// <summary>
    /// Arşivden çıkan ağacın ÖGE üst sınırı (dosya + klasör).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden gerek var:</b> bu ağaç, kullanıcının verdiği bir URL'den indirilip
    /// <c>Expand-Archive</c> ile açılan zip'in içeriğidir — yani içeriğini SALDIRGAN belirler.
    /// Milyonlarca girdi taşıyan bir arşiv ("zip bombası"), sınırsız gezilen ve sınırsız kopyalanan
    /// bir işe dönüşürdü. Kavşak riski burada YOKTUR (<c>Expand-Archive</c> yeniden ayrıştırma
    /// noktası üretmez) — ama korumayı yine ortak yürüyüşten almak, bu yolun kendi kopyasını
    /// taşımaması demektir; ağaç bir gün başka bir yerden gelirse koruma da beraberinde gelir.
    /// </para>
    /// <para>
    /// 500.000: dosya yöneticisinin 200.000'inden yüksek, çünkü burada kullanıcı zaten uzun süren
    /// bir indirme + açma işlemini beklemiştir ve içe aktarılan bir CMS ağacı (eklenti/tema
    /// klasörleriyle) yüz binlerce öge olabilir. Meşru bir içe aktarmayı reddetmek, korumanın
    /// kendisini bir arıza hâline getirirdi.
    /// </para>
    /// </remarks>
    internal const int IceAktarmaOgeButcesi = 500_000;

    /// <summary>
    /// "Arşiv çok büyük" reddinin AYRINTI satırı: etiket-değer, cümle DEĞİL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden dilden bağımsız:</b> bu depoda <c>AppException.Details</c> ÇEVRİLMEZ — arayüz onu
    /// çevirinin altına HAM basar (<c>useNotice.resolveApiMessage</c>: "çeviri + \n\n + ayrıntı",
    /// <c>t()</c>'ye hiç uğramaz). Burada eskiden Türkçe bir anlatı cümlesi duruyordu ve sonucu
    /// ölçüldü: İNGİLİZCE panelde İngilizce başlığın hemen altında Türkçe bir satır. Yeni bir
    /// i18n anahtarı çözüm değil — çevirisi olmayan anahtar ekranda HAM görünür
    /// (<c>MessageKeyCoverageTests</c> tam bunu kapatıyor).
    /// </para>
    /// <para>
    /// <b>Neden ayrı bir metot:</b> asıl fırlatma yolu (<see cref="ImportSiteArchiveAsync"/>)
    /// gerçek bir HTTP indirmesi ve yarım milyon ögelik bir ağaç gerektiriyor, yani üretilen satır
    /// oradan ölçülemez. Ayrılınca ölçülebiliyor — bu deponun <see cref="MeasureImportTree"/> ve
    /// <see cref="ApplyImportedContent"/> için verdiği kararın aynısı.
    /// </para>
    /// <para>
    /// Sayılar <c>&gt;=</c> ile yazılır ve bu bilinçli: ölçüm bütçe dolduğunda KESİLDİ, yani
    /// gerçek büyüklük en az bu kadardır — "eşittir" demek ölçülmemiş bir şeyi iddia etmek olurdu.
    /// </para>
    /// </remarks>
    internal static string CokBuyukArsivAyrintisi(SafeDirectoryWalkResult olcum, int ogeButcesi)
        => $"files>={olcum.FileCount}; dirs>={olcum.DirectoryCount}; limit={ogeButcesi}; site=untouched";

    /// <summary>
    /// Bu yolun ağaç bütçesi. <b>SÜRE sınırı bilerek YOKTUR</b> (<see cref="int.MaxValue"/>).
    /// </summary>
    /// <remarks>
    /// Burada ölçülen iş DOSYA KOPYALAMAKTIR ve süresi diskin hızına bağlıdır. Süreye bakan bir
    /// bütçe, yavaş bir diskte MEŞRU bir içe aktarmayı yarıda keserdi — ve bu yolda "yarıda" demek
    /// site kökünü geri sarmak, yani kullanıcının işini boşa çıkarmak demektir. Zip bombasının
    /// büyüklüğü ÖGE SAYISIDIR; sınırlanması gereken de odur.
    /// </remarks>
    /// <param name="ogeButcesi">Yalnız TEST içindir ve hiçbir üretim çağrısı vermez: korumanın
    /// gerçekten çalıştığı ancak bütçe DOLDURULARAK kanıtlanabilir, üretim değeriyle bu testte
    /// yarım milyon dosya yaratmak demekti. Aynı gerekçe kota/boyut yolunda da yazılı.</param>
    private static SafeDirectoryWalkBudget IceAktarmaButcesi(int? ogeButcesi = null)
        => new(ogeButcesi ?? IceAktarmaOgeButcesi, int.MaxValue);

    /// <summary>
    /// Arşivden çıkan ağacın ölçüsü: kaç dosya var ve ağaç bütçeyi AŞTI mı?
    /// </summary>
    /// <remarks>
    /// Ölçüm YIKICI ADIMDAN ÖNCE yapılır ve iki soruyu birden cevaplar: "hiç dosya çıktı mı"
    /// (bozuk/HTML indirme denetimi) ve "ağaç kabul edilebilir büyüklükte mi". İkisi ayrı ayrı
    /// ölçülseydi ağaç iki kez gezilirdi.
    /// </remarks>
    internal static SafeDirectoryWalkResult MeasureImportTree(string directory, int? ogeButcesi = null)
        => Directory.Exists(directory)
            ? SafeDirectoryWalk.Yuru(directory, IceAktarmaButcesi(ogeButcesi), CancellationToken.None)
            : new SafeDirectoryWalkResult(0, 0, 0, false, string.Empty);

    /// <summary>Ağaçtaki dosya sayısı. "Beklenen çıktı gerçekten üretildi mi" ölçümüdür.</summary>
    internal static int CountFiles(string directory) => MeasureImportTree(directory).FileCount;

    private static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Doğrulanmış arşiv içeriğini site köküne yazar.
    /// <para>
    /// F7 — YANA ALMA ARTIK TEK KAYNAKTAN. Burada <see cref="ContentSideline"/>'ın satır içi
    /// yeniden yazılmış, GERİ SARMA ÖZELLİĞİ OLMAYAN dördüncü kopyası vardı: taşıma ortasında tek
    /// bir kilitli dosya (açık bir log, IIS'in tuttuğu bir DLL) istisna atınca site kökü YARI
    /// BOŞALMIŞ kalıyordu — ne eski ne yeni hâlde. <see cref="ContentSideline.MoveAside"/> yarım
    /// kalan taşımayı kendisi geri sarar ve "başlama" der.
    /// </para>
    /// <para>
    /// Kopyalama yarıda kalırsa da site ESKİ hâline döndürülür; "yeni içerik yarım, eskisi kardeş
    /// klasörde" durumu kullanıcı için veri kaybından ayırt edilemez.
    /// </para>
    /// </summary>
    internal static SiteImportResultDto ApplyImportedContent(
        string siteName,
        string source,
        string root,
        bool cleanFirst,
        int? ogeButcesi = null)
    {
        Directory.CreateDirectory(root);

        string? sidelineDirectory = null;
        IReadOnlyList<SidelinedEntry> sidelined = Array.Empty<SidelinedEntry>();
        if (cleanFirst)
        {
            sidelineDirectory = ContentSideline.BuildSiblingPath(root, "goc_yedek");
            sidelined = ContentSideline.MoveAside(root, sidelineDirectory);
        }

        try
        {
            var copied = CopyDirectoryRecursive(source, root, ogeButcesi);
            ContentSideline.RemoveIfEmpty(sidelineDirectory);

            return new SiteImportResultDto
            {
                SiteName = siteName,
                FilesCopied = copied,
                // Panel eskiden yedeğin NEREDE olduğunu hiç söylemiyordu; kullanıcı kardeş klasörü
                // bulamayınca "içerik silindi" sanıyordu.
                BackupPath = sidelined.Count > 0 && Directory.Exists(sidelineDirectory) ? sidelineDirectory! : string.Empty
            };
        }
        catch
        {
            if (sidelined.Count > 0)
            {
                // Kök yana almadan sonra BOŞTU; içindeki her şey bu yarım kopyalamadan geldi.
                // Geri sarmadan önce temizlenmezse eski ve yeni içerik karışır.
                ClearDirectoryContents(root);
                ContentSideline.MoveBack(root, sidelined);
                ContentSideline.RemoveIfEmpty(sidelineDirectory);
            }

            throw;
        }
    }

    /// <summary>Kökün İÇİNİ boşaltır (kökün kendisi kalır). En iyi çaba: silinemeyen girdi atlanır.</summary>
    private static void ClearDirectoryContents(string root)
    {
        foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            try
            {
                if (item is DirectoryInfo directory)
                {
                    directory.Delete(true);
                }
                else
                {
                    item.Delete();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Silinemeyen girdi geri koymayı engellemesin; ContentSideline.MoveBack çakışan
                // hedefi kendisi temizlemeye çalışır, başaramazsa veri yana alma klasöründe kalır.
            }
        }
    }

    /// <summary>
    /// Kaynak ağacı hedefe kopyalar. <b>Ağacı kendi başına GEZMEZ</b> — ortak, bütçeli,
    /// bağlantı takip etmeyen yürüyüşü kullanır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Burada iki <c>...AllDirectories</c> çağrısı vardı (biri klasörler, biri dosyalar
    /// için) ve ikisinin de bütçesi yoktu: kullanıcının verdiği URL'den indirilen bir zip'in
    /// milyonlarca girdisi sınırsız gezilir ve sınırsız kopyalanırdı. Ayrıca ağaç iki kez
    /// geziliyordu; tek yürüyüş hem klasörü hem dosyayı sırayla verir (klasör her zaman
    /// içindekilerden ÖNCE bildirilir, bu <c>SafeDirectoryWalk</c> sözleşmesinin parçasıdır).
    /// </para>
    /// <para>
    /// <b>Bütçe dolarsa İSTİSNA ATILIR, kısmi kopya BIRAKILMAZ.</b> Çağıran
    /// (<see cref="ApplyImportedContent"/>) istisnayı görünce site kökünü GERİ SARAR. Sessizce
    /// kesmek, kullanıcının "içe aktarma tamamlandı" yazısına bakıp yarısı eksik bir site
    /// çalıştırması olurdu. Normal yolda bu istisna hiç görülmez: aynı ağaç, siteye
    /// dokunulmadan önce <see cref="MeasureImportTree"/> ile zaten ölçülüp reddedilmiştir —
    /// buradaki denetim, o kapıyı atlayan bir çağıran için İKİNCİ settir.
    /// </para>
    /// </remarks>
    private static int CopyDirectoryRecursive(string source, string target, int? ogeButcesi = null)
    {
        Directory.CreateDirectory(target);

        // ====================================================================================
        // KAYNAK TARAFI ZATEN KORUNUYORDU, HEDEF TARAFI KORUNMUYORDU.
        //
        // <c>SafeDirectoryWalk</c> KAYNAK ağacındaki bağlantıları eler, yani arşivden gelen içerik
        // sanal alanın dışını OKUYAMAZ. Ama yazılan yol (<c>hedef</c>) hiç denetlenmiyordu: site
        // kökünün İÇİNDE duran bir kavşak (junction), adı arşivdeki bir klasörle eşleştiği anda
        // yazmayı kendi hedefine yönlendirir. Panel LocalSystem olduğu için orada hiçbir ACL
        // durdurmaz.
        //
        // Kavşağı kim koyar: site kökünde kod çalıştıran kiracının kendisi (kavşak kurmak
        // Windows'ta ayrıcalık gerektirmez). Uç bayiye açıktır ve <c>cleanFirst=false</c> ile
        // çağrıldığında kökün mevcut içeriği — dolayısıyla kavşak — YERİNDE KALIR.
        //
        // Kural TEK KAYNAKTAN (SiteBuilderPathGuard); önbellek, aynı önekin binlerce dosyada
        // tekrarlanan denetimini bir kereye indirir (gerekçesi o metodun belgesinde).
        // ====================================================================================
        var normalizedTarget = Path.GetFullPath(target);
        var denetlenenOnekler = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var count = 0;
        var yuruyus = SafeDirectoryWalk.Yuru(
            source,
            IceAktarmaButcesi(ogeButcesi),
            oge =>
            {
                // Göreli yol, KAYNAK KÖKÜNÜN uzunluğuyla kesilerek bulunur — eski kodun kalıbı
                // aynen korundu; ortak yürüyüş her ögenin tam yolunu verir.
                var hedef = target + oge.FullName[source.Length..];
                SiteBuilderPathGuard.EnsureNoReparsePointOnPath(
                    normalizedTarget, Path.GetFullPath(hedef), denetlenenOnekler);

                if (oge is DirectoryInfo)
                {
                    Directory.CreateDirectory(hedef);
                    return;
                }

                File.Copy(oge.FullName, hedef, overwrite: true);
                count++;
            },
            null,
            CancellationToken.None);

        if (yuruyus.Truncated)
        {
            // Anahtar seçimi ölçüm kapısıyla AYNI gerekçeye dayanır (bkz. ImportSiteArchiveAsync).
            // AYRINTI da aynı gerekçeyle etiket-değer: `Details` çevrilmez, arayüz onu ham basar
            // (bkz. CokBuyukArsivAyrintisi). Türkçe cümle, İngilizce panelde yarı çevrilmiş bir
            // satır demekti.
            throw new AppException(
                yuruyus.TruncatedReasonKey,
                413,
                413,
                $"copy=truncated; written={count}; site=rolled_back");
        }

        return count;
    }

    private static string TruncateMigrationError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length > 600 ? trimmed[..600] : trimmed;
    }

    private static void Validate(string root, UpdateSiteHostingSettingsRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.RedirectUrl) &&
            (!Uri.TryCreate(request.RedirectUrl.Trim(), UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new AppException("sites.invalid_redirect_url", 400, 400);
        }

        if (request.DeniedIpAddresses.Any(item => !IPAddress.TryParse(item.Trim(), out _)))
        {
            throw new AppException("security.invalid_ip_address", 400, 400);
        }

        _ = NormalizeDefaultDocuments(request.DefaultDocuments);
        if (request.MaximumUploadBytes is < 1_048_576 or > 4_294_967_295)
        {
            throw new AppException("sites.invalid_upload_limit", 400, 400);
        }

        _ = NormalizeErrorPages(root, request.CustomErrorPages);
        _ = NormalizeMimeTypes(request.MimeTypes);
        _ = BuildHstsValue(request);
    }

    /// <summary>
    /// Özel hata sayfası girdilerini doğrular ve normalleştirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Yol KULLANICIDAN gelir</b> — kiracı buraya ne isterse yazar. İki ayrı savunma var:
    /// </para>
    /// <para>
    /// 1) Biçim: yol site köküne göre olmalı (<c>/</c> ile başlar). <c>..</c>, ters bölü, sürücü
    /// harfi (<c>:</c>) ve protokol-göreli <c>//</c> reddedilir — bunlar "site dışına çık"manın
    /// bilinen yazımlarıdır.
    /// </para>
    /// <para>
    /// 2) Kapsama: yol site köküyle birleştirilip <see cref="PathContainment"/> ile ÖLÇÜLÜR.
    /// Biçim denetimi tek başına yeterli sayılmadı; depoda bu sorunun tek doğru cevabı zaten
    /// yazılmış durumda ve kaçış yazımlarını (karışık ayırıcı, yüzde kaçışı sonrası normalleşen
    /// yollar) tek elden kapatıyor. Yeniden yazmak, birini doğru diğerini eksik yapmanın yoludur.
    /// </para>
    /// </remarks>
    internal static SiteErrorPageDto[] NormalizeErrorPages(string root, IEnumerable<SiteErrorPageDto>? values)
    {
        var pages = new List<SiteErrorPageDto>();
        var seen = new HashSet<int>();

        foreach (var entry in values ?? [])
        {
            if (entry is null)
            {
                continue;
            }

            var path = (entry.Path ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                // Boş satır "bu kaydı sil" demektir; hata değil.
                continue;
            }

            // IIS şeması statusCode için 400-999 aralığını dayatır; dışına çıkan bir değer
            // web.config'i geçersiz kılar ve site 500.19 ile TAMAMEN düşer.
            if (entry.StatusCode is < 400 or > 999)
            {
                throw new AppException("sites.invalid_error_page", 400, 400);
            }

            if (!seen.Add(entry.StatusCode))
            {
                // Aynı durum kodu iki kez: IIS koleksiyon anahtarı statusCode+subStatusCode'dur,
                // mükerrer giriş yine 500.19 demektir. Sessizce birini atmak yerine söylüyoruz.
                throw new AppException("sites.invalid_error_page", 400, 400);
            }

            // '%' YASAK: IIS şemasında httpErrors/error'ın path özniteliği expanded="true"dur, yani
            // IIS değerdeki ORTAM DEĞİŞKENLERİNİ genişletir. "%windir%" gibi bir girdi normalleştirme
            // sonrası kökün altında GÖRÜNDÜĞÜ ölçüldü (C:\site\C:\Windows\...), dolayısıyla kapsama
            // denetimi tek başına bu yazımı elemez — kaynağında kesiliyor.
            if (path.Length > 260 ||
                !path.StartsWith('/') ||
                path.StartsWith("//", StringComparison.Ordinal) ||
                path.Contains("..", StringComparison.Ordinal) ||
                path.Contains('\\') ||
                path.Contains(':') ||
                path.IndexOfAny(['%', '?', '#', '*', '"', '<', '>', '|']) >= 0 ||
                path.Any(char.IsControl))
            {
                throw new AppException("sites.invalid_error_page", 400, 400);
            }

            // ÖLÇÜLEN kapsama denetimi — biçim denetiminin üstüne ikinci hat.
            // Boş bir tedbir değil: Windows'ta ayrılmış AYGIT ADLARI ("/con", "/nul", "/lpt1")
            // yukarıdaki biçim denetimlerinin HEPSİNDEN geçer, ama Path.GetFullPath onları
            // "\\.\con" biçimine çevirir — yani yol site kökünün dışına çıkar. Bunu yalnız gerçek
            // normalleştirmeye bakan bir denetim görebilir.
            var physical = Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (!PathContainment.IsInsideRoot(root, physical))
            {
                throw new AppException("sites.invalid_error_page", 400, 400);
            }

            pages.Add(new SiteErrorPageDto { StatusCode = entry.StatusCode, Path = path });
        }

        if (pages.Count > 50)
        {
            throw new AppException("sites.invalid_error_page", 400, 400);
        }

        return [.. pages.OrderBy(page => page.StatusCode)];
    }

    /// <summary>
    /// MIME eşlemelerini doğrular ve normalleştirir (uzantı küçük harf + baştaki nokta zorunlu).
    /// </summary>
    /// <remarks>
    /// Uzantı ve tür web.config'e ÖZNİTELİK olarak yazılır. XDocument kaçışı zaten yapar, ama
    /// biçim burada da daraltılır: IIS bu iki alanı doğrulamaz ve geçersiz bir MIME türü sitenin
    /// tamamını 500.19 ile düşürür — yani "kötü girdi" değil, "site kapandı" sonucu doğurur.
    /// </remarks>
    internal static SiteMimeTypeDto[] NormalizeMimeTypes(IEnumerable<SiteMimeTypeDto>? values)
    {
        var mapped = new List<SiteMimeTypeDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in values ?? [])
        {
            if (entry is null)
            {
                continue;
            }

            var extension = (entry.Extension ?? string.Empty).Trim().ToLowerInvariant();
            var mimeType = (entry.MimeType ?? string.Empty).Trim();
            if (extension.Length == 0 && mimeType.Length == 0)
            {
                continue;
            }

            // Kullanıcı "woff2" de yazabilir ".woff2" de; IIS yalnız ikincisini kabul eder.
            if (!extension.StartsWith('.'))
            {
                extension = "." + extension;
            }

            if (!MimeExtensionPattern.IsMatch(extension) || !MimeTypePattern.IsMatch(mimeType))
            {
                throw new AppException("sites.invalid_mime_type", 400, 400);
            }

            if (!seen.Add(extension))
            {
                // Mükerrer uzantı = mükerrer koleksiyon anahtarı = 500.19.
                throw new AppException("sites.invalid_mime_type", 400, 400);
            }

            mapped.Add(new SiteMimeTypeDto { Extension = extension, MimeType = mimeType });
        }

        if (mapped.Count > 100)
        {
            throw new AppException("sites.invalid_mime_type", 400, 400);
        }

        return [.. mapped.OrderBy(item => item.Extension, StringComparer.Ordinal)];
    }

    /// <summary>
    /// <c>Strict-Transport-Security</c> başlığının değerini kurar; kapalıysa <c>null</c> döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden <c>max-age=0</c> serbest:</b> HSTS'in tek geri dönüş yolu budur. Başlığı silmek
    /// yetmez — tarayıcı süre dolana kadar (yıllarca) siteyi zorla HTTPS'e çevirmeye devam eder.
    /// Yanlışlıkla HSTS açan bir kullanıcıya "0 yaz" diyebilmek, panelin onu çıkmaza sokmamasıdır.
    /// </para>
    /// <para>
    /// <b>Neden <c>preload</c> koşullu:</b> preload listesi kaydı max-age ≥ 1 yıl VE
    /// includeSubDomains şartına bağlıdır. Bunlar olmadan yazılan <c>preload</c> hiçbir işe
    /// yaramaz ama kullanıcı korunduğunu sanır — sessiz yanlış güven, hatanın kendisinden kötüdür.
    /// </para>
    /// </remarks>
    internal static string? BuildHstsValue(UpdateSiteHostingSettingsRequest request)
    {
        if (!request.HstsEnabled)
        {
            return null;
        }

        // Üst sınır 2 yıl: RFC 6797'de sınır yok ama preload listesinin önerdiği tavan budur ve
        // daha uzun bir süre hatalı yapılandırmada geri dönüşü fiilen imkânsız kılar.
        if (request.HstsMaxAgeSeconds is < 0 or > 63_072_000)
        {
            throw new AppException("sites.invalid_hsts", 400, 400);
        }

        if (request.HstsPreload && (!request.HstsIncludeSubDomains || request.HstsMaxAgeSeconds < 31_536_000))
        {
            throw new AppException("sites.invalid_hsts", 400, 400);
        }

        var value = new StringBuilder("max-age=").Append(request.HstsMaxAgeSeconds);
        if (request.HstsIncludeSubDomains)
        {
            value.Append("; includeSubDomains");
        }

        if (request.HstsPreload)
        {
            value.Append("; preload");
        }

        return value.ToString();
    }

    // internal: web.config üretimi bu servisin ASIL davranışı ve gerçek IIS olmadan da ölçülebilir
    // olmalı (testler geçici bir klasöre yazıp XML'i doğruluyor).
    /// <param name="iisProbe">
    /// IIS'in yazılan web.config'i okuyup okuyamadığını söyler: <c>true</c> kabul, <c>false</c>
    /// ret, <c>null</c> ÖLÇÜLEMEDİ (IIS yok). <c>null</c> geçilirse IIS hiç sorulmaz.
    /// </param>
    /// <param name="geriOkuyucu">
    /// Diskten geri okuma kaynağı. Varsayılanı gerçek <c>XDocument.Load</c>'dır. Bu bir TEST
    /// KANCASI değil, ÖLÇÜLEBİLİRLİK kancasıdır: "diske inen belge, yazmak istediğimiz belge
    /// DEĞİL" durumu (yarım/bozuk yazım, başka bir sürecin araya girmesi) gerçek bir dosya
    /// sisteminde kasten üretilemez; kararın kendisi ancak buradan ölçülebilir.
    /// </param>
    internal static WebConfigWriteMeasurement UpdateWebConfig(
        string root,
        UpdateSiteHostingSettingsRequest request,
        Func<bool?>? iisProbe = null,
        Func<string, XDocument>? geriOkuyucu = null,
        Func<Stream, Stream>? configureStream = null,
        CancellationToken cancellationToken = default)
    {
        var webConfigPath = Path.Combine(root, "web.config");

        // MEVCUT BELGE OKUNAMIYORSA HİÇ YAZMAYIZ.
        //
        // Bu satır eskiden korumasızdı: dosya kilitliyse/erişilemezse ham bir IOException
        // metottan dışarı çıkıyordu — panel için ANLAMSIZ bir 500. Daha kötüsü, "üstüne yazıp
        // kurtarayım" davranışı kullanıcının web.config'ini yok ederdi. Doğru cevap: dokunma ve
        // BUNU SÖYLE.
        XDocument document;
        try
        {
            document = File.Exists(webConfigPath)
                ? XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace)
                : new XDocument(new XElement("configuration"));
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return new WebConfigWriteMeasurement
            {
                WriteFailed = true,
                Detail = $"Mevcut web.config okunamadi, HIC YAZILMADI: {exception.Message}"
            };
        }

        // KÖK ELEMAN <configuration> DEĞİLSE de dokunmayız: eskiden `!` ile geçiliyordu ve
        // NullReferenceException'a düşüyordu. Kullanıcının dosyasını "düzeltmek" için üstüne
        // yazmak, panelin yapabileceği en pahalı şeydir.
        var configuration = document.Element("configuration");
        if (configuration is null)
        {
            return new WebConfigWriteMeasurement
            {
                WriteFailed = true,
                Detail = "Mevcut web.config'in kok elemani <configuration> degil; HIC YAZILMADI."
            };
        }

        var systemWebServer = Ensure(configuration, "system.webServer");
        var security = Ensure(systemWebServer, "security");

        // KULLANILMAYAN BÖLÜM YAZILMAZ.
        //
        // ipSecurity "IP and Domain Restrictions", httpRedirect ise "HTTP Redirection" rol
        // hizmetini gerektirir; panelin kurulum betiği (scripts/enable-iis-features.ps1) bunların
        // hiçbirini kurmuyor. IIS tanımlanmamış bir config bölümü görünce sitenin TAMAMINA 500.19
        // döner — yani bir ayarı hiç kullanmayan site, sırf bölüm yazıldığı için düşüyordu.
        //
        // Yazılmadıklarında davranış aynı kalır: ipSecurity'nin varsayılanı zaten "listelenmeyene
        // izin ver", httpRedirect'inki de "kapalı". Yani bu bölümler ancak GERÇEKTEN bir şey
        // değiştirdiklerinde yazılıyor; kapatıldıklarında ise kaldırılıyor ki artık kalmasın.
        var engellenenler = request.DeniedIpAddresses.Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
        if (engellenenler.Length > 0)
        {
            var ipSecurity = Ensure(security, "ipSecurity");
            ipSecurity.SetAttributeValue("allowUnlisted", "true");
            ipSecurity.Elements("add").Remove();
            foreach (var ip in engellenenler)
            {
                ipSecurity.Add(new XElement("add", new XAttribute("ipAddress", ip), new XAttribute("allowed", "false")));
            }
        }
        else
        {
            security.Element("ipSecurity")?.Remove();
        }

        var yonlendirme = request.RedirectUrl.Trim();
        if (yonlendirme.Length > 0)
        {
            var httpRedirect = Ensure(systemWebServer, "httpRedirect");
            httpRedirect.SetAttributeValue("enabled", "true");
            httpRedirect.SetAttributeValue("destination", yonlendirme);
            httpRedirect.SetAttributeValue("exactDestination", "false");
            httpRedirect.SetAttributeValue("httpResponseStatus", "Permanent");
        }
        else
        {
            systemWebServer.Element("httpRedirect")?.Remove();
        }

        var directoryBrowse = Ensure(systemWebServer, "directoryBrowse");
        directoryBrowse.SetAttributeValue("enabled", request.DirectoryBrowsingEnabled ? "true" : "false");

        var defaultDocument = Ensure(systemWebServer, "defaultDocument");
        var defaultFiles = Ensure(defaultDocument, "files");
        defaultFiles.RemoveNodes();
        defaultFiles.Add(new XElement("clear"));
        foreach (var documentName in NormalizeDefaultDocuments(request.DefaultDocuments))
        {
            defaultFiles.Add(new XElement("add", new XAttribute("value", documentName)));
        }

        var httpErrors = Ensure(systemWebServer, "httpErrors");
        httpErrors.SetAttributeValue("errorMode", request.DetailedErrorsEnabled ? "Detailed" : "DetailedLocalOnly");
        httpErrors.SetAttributeValue("existingResponse", request.DetailedErrorsEnabled ? "PassThrough" : "Auto");

        // ÖZEL HATA SAYFALARI (durum koduna göre).
        //
        // Koleksiyonun SAHİBİ paneldir: her kayıtta önce bizim yazdıklarımız silinip liste baştan
        // kurulur. Aksi halde ekrandan kaldırılan bir girdi dosyada yaşamaya devam ederdi —
        // kullanıcı "sildim" der, IIS hâlâ eski sayfayı sunardı.
        httpErrors.Elements("error").Remove();
        httpErrors.Elements("remove").Remove();
        httpErrors.Elements("clear").Remove();
        foreach (var page in NormalizeErrorPages(root, request.CustomErrorPages))
        {
            // <remove> ŞART. IIS'in kendi applicationHost.config'i 401/403/404/405/406/412/500/501/502
            // için zaten birer <error> tanımlar; koleksiyon anahtarı statusCode+subStatusCode olduğundan
            // aynı kodu ikinci kez EKLEMEK "duplicate collection entry" -> sitenin TAMAMINA 500.19 demektir.
            // Yani <remove> olmadan "404 sayfamı değiştir" isteği siteyi düşürürdü.
            //
            // Üst yapılandırmada BULUNMAYAN bir kodu <remove> etmek zararsızdır: IIS koleksiyon birleştirmesi
            // <remove>'u bir SÜZGEÇ olarak uygular, eşleşme yoksa hiçbir şey olmaz. (Aynı kalıp ".woff2" MIME
            // düzeltmesi olarak hem uzantının tanımlı OLMADIĞI IIS 8.5'te hem tanımlı olduğu IIS 10'da
            // yıllardır aynı biçimde dağıtılıyor.)
            httpErrors.Add(new XElement("remove",
                new XAttribute("statusCode", page.StatusCode),
                new XAttribute("subStatusCode", -1)));

            // responseMode=ExecuteURL: yol aynı uygulama içinde ÇALIŞTIRILIR. File (mutlak disk yolu)
            // ve Redirect (dış URL) kasten desteklenmiyor — gerekçe SiteErrorPageDto'da.
            httpErrors.Add(new XElement("error",
                new XAttribute("statusCode", page.StatusCode),
                new XAttribute("path", page.Path),
                new XAttribute("responseMode", "ExecuteURL")));
        }

        // urlCompression sıkıştırma modüllerini gerektirir (yine kurulum betiğinde yok). Statik
        // açık + dinamik kapalı IIS'in kendi varsayılanıdır; o durumda bölüm hiç yazılmaz.
        if (request.StaticCompressionEnabled && !request.DynamicCompressionEnabled)
        {
            systemWebServer.Element("urlCompression")?.Remove();
        }
        else
        {
            var compression = Ensure(systemWebServer, "urlCompression");
            compression.SetAttributeValue("doStaticCompression", request.StaticCompressionEnabled ? "true" : "false");
            compression.SetAttributeValue("doDynamicCompression", request.DynamicCompressionEnabled ? "true" : "false");
        }

        var requestFiltering = Ensure(security, "requestFiltering");
        var requestLimits = Ensure(requestFiltering, "requestLimits");
        var uploadBytes = Math.Clamp(request.MaximumUploadBytes, 1_048_576, 4_294_967_295);
        requestLimits.SetAttributeValue("maxAllowedContentLength", uploadBytes);
        // ASP/ASPX'te de geçerli olsun: ASP.NET httpRuntime maxRequestLength (KB) IIS limitiyle birlikte yükselir
        // (varsayılan ~4MB engeli kalkar). executionTimeout büyük yüklemeler için artırılır.
        var systemWeb = Ensure(configuration, "system.web");
        var httpRuntime = Ensure(systemWeb, "httpRuntime");
        httpRuntime.SetAttributeValue("maxRequestLength", (int)Math.Min(uploadBytes / 1024, int.MaxValue));
        httpRuntime.SetAttributeValue("executionTimeout", 600);

        // MIME TÜRLERİ.
        //
        // <clear/> KULLANILMAZ: staticContent koleksiyonu temizlenirse .html/.css/.js dahil TÜM
        // varsayılan eşlemeler gider ve site her statik dosyaya 404.3 döndürür. Bu yüzden yalnız
        // bizim girdilerimiz silinip yeniden yazılır; <remove> de yine mükerrer anahtar (500.19)
        // içindir — kullanıcı var olan bir uzantıyı (.json gibi) EZMEK istediğinde tek yol budur.
        var mimeTypes = NormalizeMimeTypes(request.MimeTypes);
        var staticContent = mimeTypes.Length > 0 ? Ensure(systemWebServer, "staticContent") : systemWebServer.Element("staticContent");
        if (staticContent is not null)
        {
            staticContent.Elements("mimeMap").Remove();
            staticContent.Elements("remove").Remove();
            foreach (var mime in mimeTypes)
            {
                staticContent.Add(new XElement("remove", new XAttribute("fileExtension", mime.Extension)));
                staticContent.Add(new XElement("mimeMap",
                    new XAttribute("fileExtension", mime.Extension),
                    new XAttribute("mimeType", mime.MimeType)));
            }

            // Eşleme kalmadıysa bölümü BÜSBÜTÜN bırakma; ama <clientCache> gibi başka bir ayar
            // varsa dokunma (o ayarı silmek bizim işimiz değil).
            if (!staticContent.HasElements && !staticContent.HasAttributes)
            {
                staticContent.Remove();
            }
        }

        // HSTS.
        //
        // Yerli IIS <hsts> öğesi applicationHost.config'te yaşar ve yalnız IIS 10 1709+'ta vardır;
        // customHeaders ise her IIS sürümünde ve SİTENİN KENDİ web.config'inde çalışır — panelin
        // zaten kullandığı yol da bu (WafService X-Frame-Options'ı böyle yazıyor).
        // Başlığın düz HTTP üzerinde de gönderilmesi zararsızdır: RFC 6797, tarayıcının güvensiz
        // taşımadan gelen HSTS başlığını YOK SAYMASINI zorunlu kılar.
        var hstsValue = BuildHstsValue(request);
        var customHeaders = hstsValue is not null
            ? Ensure(Ensure(systemWebServer, "httpProtocol"), "customHeaders")
            : systemWebServer.Element("httpProtocol")?.Element("customHeaders");
        if (customHeaders is not null)
        {
            // YALNIZ kendi başlığımıza dokunuruz: customHeaders'ı WafService de kullanıyor,
            // koleksiyonu temizlemek onun güvenlik başlıklarını sessizce silerdi.
            customHeaders.Elements()
                .Where(item => string.Equals((string?)item.Attribute("name"), HstsHeaderName, StringComparison.OrdinalIgnoreCase))
                .Remove();
            if (hstsValue is not null)
            {
                customHeaders.Add(new XElement("add",
                    new XAttribute("name", HstsHeaderName),
                    new XAttribute("value", hstsValue)));
            }
        }

        var rewrite = systemWebServer.Element("rewrite");
        var rules = rewrite?.Element("rules");
        rules?.Elements("rule").Where(item => (string?)item.Attribute("name") == "AdaPanel.HotlinkProtection").Remove();
        if (request.HotlinkProtectionEnabled)
        {
            rewrite ??= Ensure(systemWebServer, "rewrite");
            rules ??= Ensure(rewrite, "rules");
            var extensions = string.Join("|", request.ProtectedExtensions
                .Select(item => Regex.Escape(item.Trim().TrimStart('.')))
                .Where(item => item.Length > 0));
            var hosts = string.Join("|", request.AllowedReferrerHosts
                .Select(item => Regex.Escape(item.Trim()))
                .Where(item => item.Length > 0));
            if (extensions.Length > 0 && hosts.Length > 0)
            {
                rules.Add(new XElement("rule",
                    new XAttribute("name", "AdaPanel.HotlinkProtection"),
                    new XAttribute("stopProcessing", "true"),
                    new XElement("match", new XAttribute("url", $"\\.({extensions})$"), new XAttribute("ignoreCase", "true")),
                    new XElement("conditions",
                        new XElement("add", new XAttribute("input", "{HTTP_REFERER}"), new XAttribute("pattern", "^$"), new XAttribute("negate", "true")),
                        new XElement("add", new XAttribute("input", "{HTTP_REFERER}"), new XAttribute("pattern", $"^https?://([^/]*\\.)?({hosts})(/|$)"), new XAttribute("negate", "true"))),
                    new XElement("action", new XAttribute("type", "CustomResponse"), new XAttribute("statusCode", "403"), new XAttribute("statusReason", "Forbidden"))));
            }
        }

        return KaydetVeGeriOku(webConfigPath, document, iisProbe, geriOkuyucu, configureStream, cancellationToken);
    }

    /// <summary>
    /// web.config'i yazar ve YAZDIGINI GERİ OKUR. Geri okunan belge yazılmak isteneni
    /// tutmuyorsa ÖNCEKİ içerik geri konur ve bu ADIYLA raporlanır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN HATA.</b> Buranın son ifadesi <c>document.Save(webConfigPath)</c> idi. Bozuk
    /// ya da yarım yazılmış bir web.config sitenin TAMAMINA <c>500.19</c> verir — panel yine
    /// "kaydedildi" derdi. <c>Validate</c> İSTEĞİ denetliyordu, ÜRETİLEN BELGEYİ değil.
    /// </para>
    /// <para>
    /// <b>GERİ ALMA, SİLME DEĞİL.</b> Bozuk yazımda dosya SİLİNMEZ: önceki içerik varsa aynen
    /// geri yazılır; dosya daha önce yoksa yerine geçerli bir boş <c>&lt;configuration/&gt;</c>
    /// konur. Site, panelin yarım yazımı yüzünden düşmüş halde bırakılmaz.
    /// </para>
    /// <para>
    /// <b>KARŞILAŞTIRMA NEDEN <c>DeepEquals</c>.</b> Bayt karşılaştırması biçimlendirme/BOM
    /// farkında yanlış alarm verirdi. Ölçülen soru "diskteki dosya, kurduğumuz belgenin AYNISI
    /// mı" — yani XML olarak ayrıştırılabiliyor mu ve aynı ağacı veriyor mu.
    /// </para>
    /// </remarks>
    private static WebConfigWriteMeasurement KaydetVeGeriOku(
        string webConfigPath,
        XDocument document,
        Func<bool?>? iisProbe,
        Func<string, XDocument>? geriOkuyucu,
        Func<Stream, Stream>? configureStream,
        CancellationToken cancellationToken)
    {
        // ÖNCEKİ İÇERİK: geri alma bunun üzerinden yapılır (yoksa null).
        byte[]? oncekiIcerik = null;
        try
        {
            if (File.Exists(webConfigPath))
            {
                oncekiIcerik = File.ReadAllBytes(webConfigPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Okunamayan bir dosyayı geri de koyamayız; bunu ölçüme yazıp devam ederiz.
            return WebConfigWriteMeasurement.Olculemedi($"Onceki web.config okunamadi: {exception.Message}");
        }

        try
        {
            WebConfigurationFile.Save(webConfigPath, document, configureStream, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Publication did not complete: report failure, never a saved measurement.
            return new WebConfigWriteMeasurement
            {
                WriteFailed = true,
                Detail = $"web.config yazilamadi: {exception.Message}"
            };
        }

        // GERİ OKUMA. Whitespace korunmadan okunur; karşılaştırılan taraf da aynı biçimde
        // yeniden ayrıştırılır ki fark ANLAM farkı olsun, boşluk farkı değil.
        XDocument diskten;
        try
        {
            diskten = geriOkuyucu is null
                ? XDocument.Load(webConfigPath, LoadOptions.None)
                : geriOkuyucu(webConfigPath);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            var geriKondu = OncekineDon(webConfigPath, oncekiIcerik);
            return new WebConfigWriteMeasurement
            {
                Reparsed = false,
                RolledBack = geriKondu,
                Detail = $"Yazilan web.config GERI OKUNAMADI ({exception.Message}). "
                         + (geriKondu ? "Onceki icerik geri konuldu." : "GERI ALINAMADI.")
            };
        }

        var beklenen = XDocument.Parse(document.ToString(SaveOptions.None), LoadOptions.None);
        var esit = XNode.DeepEquals(beklenen.Root, diskten.Root);
        if (esit)
        {
            // İKİNCİ KAPI: IIS belgeyi okuyabiliyor mu? Reddediyorsa site 500.19 verecek demektir;
            // o hâlde ÖNCEKİ içerik geri konur — panelin yazımı siteyi düşürmüş hâlde bırakmaz.
            var iisKabul = iisProbe?.Invoke();
            if (iisKabul is false)
            {
                var iisGeriAlindi = OncekineDon(webConfigPath, oncekiIcerik);
                return new WebConfigWriteMeasurement
                {
                    Reparsed = true,
                    Matches = true,
                    IisProbed = true,
                    IisAccepted = false,
                    RolledBack = iisGeriAlindi,
                    Detail = "IIS yazilan web.config'i OKUYAMADI (500.19 sinifi). "
                             + (iisGeriAlindi ? "Onceki icerik geri konuldu." : "GERI ALINAMADI.")
                };
            }

            return new WebConfigWriteMeasurement
            {
                Reparsed = true,
                Matches = true,
                IisProbed = iisKabul is not null,
                IisAccepted = iisKabul is true
            };
        }

        var alindi = OncekineDon(webConfigPath, oncekiIcerik);
        return new WebConfigWriteMeasurement
        {
            Reparsed = true,
            Matches = false,
            RolledBack = alindi,
            Detail = "Diskten okunan web.config yazilmak istenen belge DEGIL. "
                     + (alindi ? "Onceki icerik geri konuldu." : "GERI ALINAMADI.")
        };
    }

    /// <summary>
    /// IIS yazılan web.config'i OKUYABİLİYOR mu? <c>true</c> kabul · <c>false</c> ret ·
    /// <c>null</c> ÖLÇÜLEMEDİ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bir web.config XML olarak kusursuz ama IIS için ÖLÜMCÜL olabilir: tanımlanmamış bir
    /// bölüm (<c>ipSecurity</c>, <c>httpRedirect</c>… rol hizmeti kurulu değilse) sitenin
    /// TAMAMINA <c>500.19</c> verir. Bunu ancak yapılandırma sistemini okuyan taraf söyler.
    /// </para>
    /// <para>
    /// <b>ÜÇ DEĞER ŞART.</b> IIS yoksa (<c>sites.iis_not_available</c>) ya da site bulunamıyorsa
    /// cevap "reddetti" DEĞİL, <c>null</c>'dır. İkisini aynı görüntüye düşürmek, IIS'siz bir
    /// makinede her kaydı sahte bir hatayla reddetmek olurdu.
    /// </para>
    /// </remarks>
    internal static bool? IisWebConfigOkuyabiliyorMu(string siteName)
    {
        try
        {
            using var serverManager = AspNet.IisConfigurationAccess.CreateServerManager();
            // Bölümü GERÇEKTEN okuruz: yalnız yapılandırmayı almak tembel olabilir, bölüm
            // çözümlemesi ise bozuk/tanımsız bölümde patlar — ölçmek istediğimiz tam olarak bu.
            // Koleksiyonu saymak, tembel çözümlemenin gerçekten koştuğunu garantiler.
            _ = AspNet.IisConfigurationAccess
                .SnapshotHandlers(AspNet.IisConfigurationAccess.GetHandlersCollection(serverManager, siteName))
                .Count;
            return true;
        }
        catch (Exception exception)
        {
            // VARSAYILAN "ÖLÇÜLEMEDİ"DİR, "REDDEDİLDİ" DEĞİL.
            //
            // Bu ayrım hayatidir: yükseltilmemiş bir süreçte applicationHost.config okunamaz ve
            // erişim reddini "web.config bozuk" saymak, panelin HER kaydı sahte bir hatayla
            // reddetmesi demek olurdu. Bu yüzden yalnız TANIDIĞIMIZ bir yapılandırma hatası
            // "reddedildi"ye sayılır; geri kalan her şey ölçülemedi'ye düşer.
            return YapilandirmaHatasiMi(exception) ? false : null;
        }
    }

    /// <summary>
    /// İstisna, IIS yapılandırma sisteminin belgeyi ÇÖZÜMLEYEMEDİĞİNİ mi söylüyor?
    /// (<c>Microsoft.Web.Administration.ServerManagerException</c> ve türevleri.)
    /// </summary>
    private static bool YapilandirmaHatasiMi(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var tur = current.GetType().FullName ?? string.Empty;
            if (tur.Contains("ServerManagerException", StringComparison.Ordinal) ||
                tur.Contains("FileLoadException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Önceki içeriği geri yazar; dosya daha önce yoksa geçerli boş bir belge bırakır.</summary>
    private static bool OncekineDon(string webConfigPath, byte[]? oncekiIcerik)
    {
        try
        {
            if (oncekiIcerik is not null)
            {
                WebConfigurationFile.WriteBytes(webConfigPath, oncekiIcerik);
                return true;
            }

            // Dosya YOKTU. Silmek yerine ZARARSIZ ve GEÇERLİ bir belge bırakılır: bozuk bir
            // web.config siteyi 500.19 ile düşürür, boş bir tanesi hiçbir şey değiştirmez.
            WebConfigurationFile.Save(webConfigPath, new XDocument(new XElement("configuration")));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string[] NormalizeDefaultDocuments(IEnumerable<string> values)
    {
        var documents = values
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (documents.Length == 0 ||
            documents.Any(item =>
                item.Length > 128 ||
                item.Contains('/') ||
                item.Contains('\\') ||
                item.Contains("..", StringComparison.Ordinal)))
        {
            throw new AppException("sites.invalid_default_document", 400, 400);
        }

        return documents;
    }

    private static void UpdateIisLimits(string siteName, long bandwidth, long connections)
    {
        var assemblyPath = Environment.ExpandEnvironmentVariables(WebAdministrationAssemblyPath);
        if (!File.Exists(assemblyPath))
        {
            throw new AppException("sites.iis_not_available", 503, 503);
        }

        var assembly = Assembly.LoadFrom(assemblyPath);
        var managerType = assembly.GetType("Microsoft.Web.Administration.ServerManager")!;
        using var manager = (IDisposable)Activator.CreateInstance(managerType)!;
        var sites = GetPropertyValue(managerType, manager, "Sites");
        var site = sites.GetType().GetProperty("Item", [typeof(string)])!.GetValue(sites, [siteName])
            ?? throw new AppException("sites.site_not_found", 404, 404);
        var limits = GetPropertyValue(site.GetType(), site, "Limits");
        SetPropertyValue(limits.GetType(), limits, "MaximumBandwidth", (ulong)Math.Max(0, bandwidth));
        SetPropertyValue(limits.GetType(), limits, "MaximumConnections", (ulong)Math.Max(0, connections));
        InvokeMethod(managerType, manager, "CommitChanges");
    }

    private static object GetPropertyValue(Type targetType, object target, string propertyName)
    {
        var property = targetType.GetProperty(propertyName)
            ?? throw new MissingMemberException(targetType.FullName, propertyName);

        try
        {
            return property.GetValue(target)
                ?? throw new AppException("sites.write_failed", 500, 500);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static void SetPropertyValue(Type targetType, object target, string propertyName, object value)
    {
        var property = targetType.GetProperty(propertyName)
            ?? throw new MissingMemberException(targetType.FullName, propertyName);

        try
        {
            property.SetValue(target, value);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static void InvokeMethod(Type targetType, object target, string methodName)
    {
        var method = targetType.GetMethod(methodName)
            ?? throw new MissingMethodException(targetType.FullName, methodName);

        try
        {
            method.Invoke(target, null);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static AppException MapIisException(Exception exception, string fallbackMessageKey)
    {
        // Dile bağımsız (HRESULT + İngilizce) sınıflandırma — Türkçe Windows'ta da doğru çalışır.
        var message = Support.Win32ErrorHelper.Flatten(exception);
        if (Support.Win32ErrorHelper.IsAccessDenied(message))
        {
            return CreateDiagnosticException("sites.iis_permission_denied", 403, exception);
        }

        if (Support.Win32ErrorHelper.IsInUse(message))
        {
            return CreateDiagnosticException("sites.iis_configuration_locked", 409, exception);
        }

        if (message.Contains("section", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("applicationHost.config", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.iis_install_required", 503, exception);
        }

        return CreateDiagnosticException(fallbackMessageKey, 500, exception);
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

    private static XElement Ensure(XElement parent, string name)
    {
        var element = parent.Element(name);
        if (element is not null)
        {
            return element;
        }

        element = new XElement(name);
        parent.Add(element);
        return element;
    }
}
