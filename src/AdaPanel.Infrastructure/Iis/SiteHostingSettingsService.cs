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
    JsonFileStore fileStore) : ISiteHostingSettingsService
{
    private readonly string dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));
    private const string StatePath = "config/site-hosting-settings.json";
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";

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
        UpdateWebConfig(site.PhysicalPath, ToUpdateRequest(state));
        return state;
    }

    public async Task<SiteHostingSettingsDto> UpdateAsync(
        string siteName,
        UpdateSiteHostingSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        Validate(request);
        UpdateWebConfig(site.PhysicalPath, request);
        try
        {
            UpdateIisLimits(siteName, request.MaximumBandwidthBytesPerSecond, request.MaximumConnections);
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
            ProtectedExtensions = request.ProtectedExtensions.Select(item => item.Trim().TrimStart('.')).Where(item => item.Length > 0).Distinct().ToArray()
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

        var result = await ProcessRunner.RunAsync(
            phpPath,
            $"\"{composerPath}\" install --no-interaction --prefer-dist --optimize-autoloader",
            string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath,
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

        var result = await ProcessRunner.RunAsync(
            phpPath,
            $"\"{composerPath}\" {trimmed} --no-interaction --no-ansi",
            string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath,
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

        var result = await ProcessRunner.RunAsync(
            phpPath,
            $"\"{artisanPath}\" {trimmed} --no-interaction --no-ansi",
            workDir,
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

        var result = await ProcessRunner.RunAsync(
            nodeExe,
            $"\"{npmCli}\" {trimmed} --no-fund --no-audit",
            workDir,
            cancellationToken,
            timeout: TimeSpan.FromMinutes(20));

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

        // Plesk Kuyruk gibi: minutely zamanlanmış görev + --max-time = kendini yenileyen kalıcı worker.
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

        // Plesk Laravel Toolkit "Scheduler": dakikada bir `php artisan schedule:run` (Laravel'in tek cron girdisi).
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

        var result = await ProcessRunner.RunAsync(
            phpPath,
            $"\"{wpCli}\" {trimmed} --no-color",
            workDir,
            cancellationToken,
            timeout: TimeSpan.FromMinutes(10));

        return new ComposerRunResultDto
        {
            ExitCode = result.ExitCode,
            Output = result.StandardOutput,
            Error = result.StandardError
        };
    }

    // wp-cli.phar yoksa ilk kullanımda resmi build'den indirir (Plesk WP Toolkit'in altında da wp-cli vardır).
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
            var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            if (!result.Success || !Directory.Exists(extractDir))
            {
                throw new AppException("migration.download_failed", 502, 502, TruncateMigrationError(result.StandardError));
            }

            // Arşivde tek üst klasör varsa onun içeriğini kök kabul et (yaygın "site/..." paketlemesi).
            var source = extractDir;
            var topLevel = new DirectoryInfo(extractDir).GetFileSystemInfos();
            if (topLevel.Length == 1 && topLevel[0] is DirectoryInfo onlyDirectory)
            {
                source = onlyDirectory.FullName;
            }

            if (cleanFirst)
            {
                // Hedef içeriğini kardeş "_goc_yedek_<tarih>" klasörüne taşı (root DIŞINDA → root boşalır).
                var backup = $"{root.TrimEnd('\\', '/')}_goc_yedek_{DateTime.Now:yyyyMMdd_HHmmss}";
                Directory.CreateDirectory(backup);
                foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos())
                {
                    var destination = Path.Combine(backup, item.Name);
                    if (item is DirectoryInfo directory)
                    {
                        Directory.Move(directory.FullName, destination);
                    }
                    else
                    {
                        File.Move(item.FullName, destination);
                    }
                }
            }

            var copied = CopyDirectoryRecursive(source, root);
            return new SiteImportResultDto { SiteName = site.Name, FilesCopied = copied };
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

    private static void Validate(UpdateSiteHostingSettingsRequest request)
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
    }

    private static void UpdateWebConfig(string root, UpdateSiteHostingSettingsRequest request)
    {
        var webConfigPath = Path.Combine(root, "web.config");
        var document = File.Exists(webConfigPath)
            ? XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("configuration"));
        var configuration = document.Element("configuration")!;
        var systemWebServer = Ensure(configuration, "system.webServer");
        var security = Ensure(systemWebServer, "security");
        var ipSecurity = Ensure(security, "ipSecurity");
        ipSecurity.SetAttributeValue("allowUnlisted", "true");
        ipSecurity.Elements("add").Remove();
        foreach (var ip in request.DeniedIpAddresses.Select(item => item.Trim()).Where(item => item.Length > 0))
        {
            ipSecurity.Add(new XElement("add", new XAttribute("ipAddress", ip), new XAttribute("allowed", "false")));
        }

        var httpRedirect = Ensure(systemWebServer, "httpRedirect");
        httpRedirect.SetAttributeValue("enabled", string.IsNullOrWhiteSpace(request.RedirectUrl) ? "false" : "true");
        httpRedirect.SetAttributeValue("destination", request.RedirectUrl.Trim());
        httpRedirect.SetAttributeValue("exactDestination", "false");
        httpRedirect.SetAttributeValue("httpResponseStatus", "Permanent");

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

        var compression = Ensure(systemWebServer, "urlCompression");
        compression.SetAttributeValue("doStaticCompression", request.StaticCompressionEnabled ? "true" : "false");
        compression.SetAttributeValue("doDynamicCompression", request.DynamicCompressionEnabled ? "true" : "false");

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

    private static UpdateSiteHostingSettingsRequest ToUpdateRequest(SiteHostingSettingsDto state)
    {
        return new UpdateSiteHostingSettingsRequest
        {
            MaximumBandwidthBytesPerSecond = state.MaximumBandwidthBytesPerSecond,
            MaximumConnections = state.MaximumConnections,
            DeniedIpAddresses = state.DeniedIpAddresses,
            RedirectUrl = state.RedirectUrl,
            DirectoryBrowsingEnabled = state.DirectoryBrowsingEnabled,
            DefaultDocuments = state.DefaultDocuments,
            DetailedErrorsEnabled = state.DetailedErrorsEnabled,
            StaticCompressionEnabled = state.StaticCompressionEnabled,
            DynamicCompressionEnabled = state.DynamicCompressionEnabled,
            MaximumUploadBytes = state.MaximumUploadBytes,
            HotlinkProtectionEnabled = state.HotlinkProtectionEnabled,
            AllowedReferrerHosts = state.AllowedReferrerHosts,
            ProtectedExtensions = state.ProtectedExtensions
        };
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
