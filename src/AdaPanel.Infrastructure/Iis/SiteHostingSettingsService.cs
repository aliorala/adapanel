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
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Iis;

public sealed class SiteHostingSettingsService(
    IIisSiteService iisSiteService,
    IComponentBootstrapper componentBootstrapper,
    ISchedulerService schedulerService,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    IConcurrencyLock concurrencyLock,
    JsonFileStore fileStore) : ISiteHostingSettingsService
{
    private readonly string dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));
    private const string StatePath = "config/site-hosting-settings.json";
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string HstsHeaderName = "Strict-Transport-Security";

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
        UpdateWebConfig(site.PhysicalPath, request);
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
        await schedulerService.CreateTaskAsync(new CreateScheduledTaskRequest
        {
            Name = QueueTaskName(siteName),
            Command = phpPath,
            Arguments = "artisan queue:work --tries=3 --sleep=3 --max-time=3600",
            WorkingDirectory = workDir,
            ScheduleType = "minutely",
            Interval = 1
        }, cancellationToken);

        return new QueueWorkerStatusDto { Running = true, TaskName = QueueTaskName(siteName) };
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
        try
        {
            await schedulerService.DeleteTaskAsync(taskName, cancellationToken);
        }
        catch (AppException)
        {
            // Görev zaten yoksa sorun değil.
        }

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

        return new QueueWorkerStatusDto { Running = false, TaskName = taskName };
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
        await schedulerService.CreateTaskAsync(new CreateScheduledTaskRequest
        {
            Name = SchedulerTaskName(siteName),
            Command = phpPath,
            Arguments = "artisan schedule:run",
            WorkingDirectory = workDir,
            ScheduleType = "minutely",
            Interval = 1
        }, cancellationToken);

        return new QueueWorkerStatusDto { Running = true, TaskName = SchedulerTaskName(siteName) };
    }

    public async Task<QueueWorkerStatusDto> StopLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var taskName = SchedulerTaskName(siteName);
        try
        {
            await schedulerService.DeleteTaskAsync(taskName, cancellationToken);
        }
        catch (AppException)
        {
            // Görev zaten yoksa sorun değil.
        }

        return new QueueWorkerStatusDto { Running = false, TaskName = taskName };
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

        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        Directory.CreateDirectory(root);

        var work = Path.Combine(dataRoot, "temp", "migrate", Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(work, "extract");
        var archive = Path.Combine(work, "site-archive.zip");
        Directory.CreateDirectory(extractDir);

        try
        {
            var script =
                "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; " +
                $"Invoke-WebRequest -Uri '{url}' -OutFile '{archive.Replace("'", "''")}' -UseBasicParsing; " +
                $"Expand-Archive -Path '{archive.Replace("'", "''")}' -DestinationPath '{extractDir.Replace("'", "''")}' -Force";

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
            var availableFiles = source is null ? 0 : CountFiles(source);
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

    /// <summary>Ağaçtaki dosya sayısı. "Beklenen çıktı gerçekten üretildi mi" ölçümüdür.</summary>
    internal static int CountFiles(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count()
            : 0;

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
    internal static SiteImportResultDto ApplyImportedContent(string siteName, string source, string root, bool cleanFirst)
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
            var copied = CopyDirectoryRecursive(source, root);
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

    private static int CopyDirectoryRecursive(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(target + directory[source.Length..]);
        }

        var count = 0;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, target + file[source.Length..], overwrite: true);
            count++;
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
    internal static void UpdateWebConfig(string root, UpdateSiteHostingSettingsRequest request)
    {
        var webConfigPath = Path.Combine(root, "web.config");
        var document = File.Exists(webConfigPath)
            ? XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("configuration"));
        var configuration = document.Element("configuration")!;
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

        document.Save(webConfigPath);
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
