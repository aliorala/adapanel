using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Store;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.AppStore;

public sealed class AppStoreService(
    IIisSiteService iisSiteService,
    IHttpClientFactory httpClientFactory,
    IComponentBootstrapper componentBootstrapper,
    IPhpService phpService,
    ISchedulerService schedulerService,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<AppStoreService> logger) : IAppStoreService
{
    private static readonly SemaphoreSlim LaravelInstallLock = new(1, 1);
    private static readonly HashSet<string> ReplaceableStarterFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "index.html",
        "index.php",
        "web.config",
        "Default.aspx",
        "default.asp"
    };

    private static readonly AppTemplateDto[] Templates =
    [
        new()
        {
            Id = "laravel",
            DisplayName = "Laravel",
            Description = "Laravel 13 uygulamasini uretim ayarlari, SQLite, IIS Rewrite ve guvenli public web koku ile hazirlar.",
            Runtime = "PHP 8.3+",
            Category = "Framework",
            DownloadUrl = "https://packagist.org/packages/laravel/laravel",
            RequiresDatabase = true,
            DefaultRootFolder = "public",
            Requirements = ["PHP 8.3+", "Composer", "IIS URL Rewrite"],
            AutomaticSetup = ["SQLite", "APP_KEY", "Migration", "FastCGI", "Scheduler", "Klasor izinleri"],
            Featured = true
        },
        new()
        {
            Id = "wordpress",
            DisplayName = "WordPress",
            Description = "WordPress son surum paketini site kokune indirip acar.",
            Runtime = "PHP",
            Category = "CMS",
            DownloadUrl = "https://wordpress.org/latest.zip",
            RequiresDatabase = true,
            DefaultRootFolder = "wordpress",
            Requirements = ["PHP", "MySQL veya MariaDB"],
            AutomaticSetup = ["Paket indirme", "Dosyalari acma"]
        },
        new()
        {
            Id = "roundcube",
            DisplayName = "Roundcube Webmail",
            Description = "Roundcube webmail'i site kokune kurar; SQLite ile calisir ve yerel Stalwart IMAP/SMTP'ye (STARTTLS) otomatik baglanir.",
            Runtime = "PHP 8.x",
            Category = "Mail",
            DownloadUrl = "https://github.com/roundcube/roundcubemail/releases/latest",
            RequiresDatabase = false,
            DefaultRootFolder = "webmail",
            Requirements = ["PHP 8.x", "Stalwart Mail Server (IMAP/SMTP)"],
            AutomaticSetup = ["Paket indirme", "SQLite", "config.inc.php", "IMAP/SMTP=localhost STARTTLS", "FastCGI", "Klasor izinleri"],
            Featured = true
        },
        new()
        {
            Id = "adminer",
            DisplayName = "Adminer",
            Description = "Tek dosyalik Adminer veritabani arayuzunu hedef klasore yerlestirir.",
            Runtime = "PHP",
            Category = "Database",
            DownloadUrl = "https://github.com/vrana/adminer/releases/download/v5.4.2/adminer-5.4.2.php",
            RequiresDatabase = false,
            DefaultRootFolder = "adminer",
            Requirements = ["PHP"],
            AutomaticSetup = ["Tek dosya dagitimi"]
        },
        new()
        {
            Id = "plain-php",
            DisplayName = "PHP Starter",
            Description = "Basit bir index.php ile bos PHP calisma alani olusturur.",
            Runtime = "PHP",
            Category = "Starter",
            DownloadUrl = string.Empty,
            RequiresDatabase = false,
            DefaultRootFolder = "public",
            Requirements = ["PHP"],
            AutomaticSetup = ["Baslangic dosyasi"]
        },
        new()
        {
            Id = "aspnet-static",
            DisplayName = "ASP.NET Starter",
            Description = "Basit web.config ile statik veya ASP.NET uygulama kokunu hazirlar.",
            Runtime = "ASP.NET",
            Category = "Starter",
            DownloadUrl = string.Empty,
            RequiresDatabase = false,
            DefaultRootFolder = "site",
            Requirements = ["IIS"],
            AutomaticSetup = ["web.config", "Baslangic sayfasi"]
        }
    ];

    private readonly string dataRoot = Path.GetFullPath(
        Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));

    public Task<IReadOnlyCollection<AppTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<AppTemplateDto>>(Templates);
    }

    public async Task<AppTemplateDto> InstallAsync(InstallAppTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var templateId = request.TemplateId?.Trim().ToLowerInvariant() ?? string.Empty;
        var template = Templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new AppException("store.template_not_found", 404, 404);

        if (template.Id == "laravel")
        {
            await LaravelInstallLock.WaitAsync(cancellationToken);
            try
            {
                await InstallLaravelAsync(request, cancellationToken);
            }
            finally
            {
                LaravelInstallLock.Release();
            }

            return template;
        }

        if (template.Id == "roundcube")
        {
            await InstallRoundcubeAsync(request, cancellationToken);
            return template;
        }

        var destination = await ResolveDestinationAsync(request, cancellationToken);
        Directory.CreateDirectory(destination);

        switch (template.Id)
        {
            case "wordpress":
                await InstallWordPressAsync(destination, cancellationToken);
                break;
            case "adminer":
                await InstallVerifiedAdminerAsync(destination, cancellationToken);
                break;
            case "plain-php":
                await File.WriteAllTextAsync(Path.Combine(destination, "index.php"), "<?php phpinfo();", cancellationToken);
                break;
            case "aspnet-static":
                await File.WriteAllTextAsync(Path.Combine(destination, "web.config"), """
                    <?xml version="1.0" encoding="utf-8"?>
                    <configuration>
                      <system.webServer>
                        <defaultDocument>
                          <files>
                            <add value="index.html" />
                          </files>
                        </defaultDocument>
                      </system.webServer>
                    </configuration>
                    """, cancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(destination, "index.html"),
                    "<h1>AdaPanel ASP.NET starter</h1>",
                    cancellationToken);
                break;
        }

        // TÜM PHP app'leriyle ortak güvence: placeholder index.html kaldır + index.php varsayılan
        // belge (yoksa). index.php olmayan kökte (aspnet-static) no-op. Laravel/Roundcube kendi
        // web.config'ini (index.php'li) yazdığından korunur.
        Iis.WebAppPublisher.EnsurePhpEntry(destination);

        return template;
    }

    private async Task InstallWordPressAsync(string destination, CancellationToken cancellationToken)
    {
        var package = await ResolveWordPressPackageAsync(cancellationToken);
        var workspace = Path.Combine(dataRoot, "temp", "app-installs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            await DownloadAndExtractZipAsync(
                package.DownloadUrl,
                package.PackageSha1,
                workspace,
                cancellationToken);
            FlattenSingleTopDirectory(workspace);
            await VerifyWordPressFilesAsync(workspace, package.Version, cancellationToken);
            CopyDirectory(workspace, destination);
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    private async Task InstallVerifiedAdminerAsync(string destination, CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var installed = FindInstalled(catalog, "adminer");
        if (installed is null)
        {
            await componentBootstrapper.InstallAsync("adminer", null, null, cancellationToken);
            catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
            installed = FindInstalled(catalog, "adminer");
        }

        var source = installed is null
            ? string.Empty
            : Path.Combine(installed.InstallDir, "adminer.php");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            throw new AppException("store.dependencies_failed", 500, 500);
        }

        File.Copy(source, Path.Combine(destination, "index.php"), overwrite: true);
    }

    private async Task InstallRoundcubeAsync(InstallAppTemplateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SiteName))
        {
            throw new AppException("store.site_required", 400, 400);
        }

        var site = await iisSiteService.GetSiteAsync(request.SiteName.Trim(), cancellationToken);
        var applicationRoot = ResolveApplicationRoot(site);
        EnsureLaravelDestinationReady(applicationRoot, request.CleanInstall);

        var php = await EnsurePhpRuntimeAsync(cancellationToken);
        var workspace = Path.Combine(dataRoot, "temp", "app-installs", Guid.NewGuid().ToString("N"));
        var stagingRoot = Path.Combine(workspace, "roundcube");
        Directory.CreateDirectory(stagingRoot);
        DeploymentState? deployment = null;

        try
        {
            var downloadUrl = await ResolveRoundcubeDownloadUrlAsync(cancellationToken);
            await DownloadAndExtractTarGzAsync(downloadUrl, stagingRoot, cancellationToken);
            FlattenSingleTopDirectory(stagingRoot);
            ValidateRoundcubeProject(stagingRoot);
            await WriteRoundcubeConfigAsync(stagingRoot, cancellationToken);
            await WriteRoundcubeWebConfigAsync(stagingRoot, cancellationToken);

            deployment = DeployApplication(stagingRoot, applicationRoot, workspace);
            await InitRoundcubeSqliteAsync(php.PhpExecutable, applicationRoot, cancellationToken);
            await ApplyRoundcubeAclAsync(applicationRoot, site.ApplicationPoolName, cancellationToken);
            await phpService.SetSitePhpVersionAsync(
                site.Name,
                new SetSitePhpVersionRequest { ComponentId = php.ComponentId },
                cancellationToken);
        }
        catch (AppException)
        {
            await RollbackLaravelDeploymentAsync(site, deployment, cancellationToken);
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RollbackLaravelDeploymentAsync(site, deployment, cancellationToken);
            throw new AppException("store.install_failed", 500, 500, BuildDiagnosticDetails(exception));
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    private async Task<(string ComponentId, string PhpExecutable)> EnsurePhpRuntimeAsync(CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var installed = catalog.Installed
            .Where(item =>
                item.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Status, "Installed", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => ParsePhpVersion(item.ComponentId))
            .FirstOrDefault();

        if (installed is null)
        {
            var manifest = catalog.Manifest
                .Where(item => item.Id.StartsWith("php-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => ParsePhpVersion(item.Id))
                .FirstOrDefault()
                ?? throw new AppException("store.php_unsupported", 409, 409);

            await phpService.InstallVersionAsync(manifest.Id, null, null, cancellationToken);
            catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
            installed = FindInstalled(catalog, manifest.Id);
        }
        else
        {
            await phpService.RegisterFastCgiAsync(installed.ComponentId, cancellationToken);
        }

        if (installed is null)
        {
            throw new AppException("store.dependencies_failed", 500, 500);
        }

        await ComponentBootstrapper.EnsurePhpIniAsync(installed.InstallDir, cancellationToken);
        var phpExecutable = Path.Combine(installed.InstallDir, "php.exe");
        if (!File.Exists(phpExecutable))
        {
            throw new AppException("store.dependencies_failed", 500, 500);
        }

        return (installed.ComponentId, phpExecutable);
    }

    private async Task<string> ResolveRoundcubeDownloadUrlAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        // ResponseHeadersRead ile yalnız bağlan+header fazını sınırlar (gövde indirme sınırsız kalır);
        // Infinite'te takılı sunucuya bağlanış sonsuza asılırdı.
        client.Timeout = TimeSpan.FromMinutes(2);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/repos/roundcube/roundcubemail/releases/latest");
        request.Headers.UserAgent.ParseAdd("AdaPanel");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AppException("store.download_failed", (int)response.StatusCode, 502);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("assets", out var assets))
        {
            throw new AppException("store.download_not_found", 404, 404);
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name) &&
                name.StartsWith("roundcubemail-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("-complete.tar.gz", StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("browser_download_url", out var urlElement))
            {
                return urlElement.GetString() ?? throw new AppException("store.download_not_found", 404, 404);
            }
        }

        throw new AppException("store.download_not_found", 404, 404);
    }

    private async Task DownloadAndExtractTarGzAsync(string url, string destination, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        // ResponseHeadersRead ile yalnız bağlan+header fazını sınırlar (gövde indirme sınırsız kalır);
        // Infinite'te takılı sunucuya bağlanış sonsuza asılırdı.
        client.Timeout = TimeSpan.FromMinutes(2);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureDownloadSuccess(response);

        var tempFile = Path.GetTempFileName();
        try
        {
            await using (var stream = File.Create(tempFile))
            {
                await response.Content.CopyToAsync(stream, cancellationToken);
            }

            await using var archive = File.OpenRead(tempFile);
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: true, cancellationToken);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static void ValidateRoundcubeProject(string root)
    {
        if (!File.Exists(Path.Combine(root, "index.php")) ||
            !File.Exists(Path.Combine(root, "config", "config.inc.php.sample")) ||
            !File.Exists(Path.Combine(root, "SQL", "sqlite.initial.sql")))
        {
            throw new AppException("store.invalid_package", 500, 500);
        }
    }

    private static async Task WriteRoundcubeConfigAsync(string root, CancellationToken cancellationToken)
    {
        var desKey = GenerateDesKey();
        var configContent = $$"""
            <?php
            /* AdaPanel tarafindan olusturuldu */
            $config = [];
            $config['db_dsnw'] = 'sqlite:///' . dirname(__DIR__) . '/temp/roundcube.db?mode=0646';
            /* Stalwart duz metin portta LOGIN'i reddeder: STARTTLS (tls://) zorunlu.
               Sertifika kendinden imzali oldugundan dogrulama kapatilir (yerel baglanti). */
            $config['imap_host'] = 'tls://localhost:143';
            $config['imap_conn_options'] = ['ssl' => ['verify_peer' => false, 'verify_peer_name' => false, 'allow_self_signed' => true]];
            $config['smtp_host'] = 'tls://localhost:587';
            $config['smtp_conn_options'] = ['ssl' => ['verify_peer' => false, 'verify_peer_name' => false, 'allow_self_signed' => true]];
            $config['smtp_user'] = '%u';
            $config['smtp_pass'] = '%p';
            $config['support_url'] = '';
            $config['product_name'] = 'AdaPanel Webmail';
            $config['des_key'] = '{{desKey}}';
            $config['plugins'] = ['archive', 'zipdownload'];
            $config['skin'] = 'elastic';
            $config['enable_installer'] = false;
            $config['log_driver'] = 'file';
            $config['temp_dir'] = dirname(__DIR__) . '/temp';
            """;

        var configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(configDirectory, "config.inc.php"),
            configContent,
            new UTF8Encoding(false),
            cancellationToken);
    }

    private static async Task WriteRoundcubeWebConfigAsync(string root, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "web.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <system.webServer>
                <defaultDocument>
                  <files>
                    <clear />
                    <add value="index.php" />
                  </files>
                </defaultDocument>
                <security>
                  <requestFiltering removeServerHeader="true">
                    <hiddenSegments>
                      <add segment="config" />
                      <add segment="temp" />
                      <add segment="logs" />
                      <add segment="SQL" />
                    </hiddenSegments>
                  </requestFiltering>
                </security>
                <httpProtocol>
                  <customHeaders>
                    <remove name="X-Powered-By" />
                    <add name="X-Content-Type-Options" value="nosniff" />
                    <add name="X-Frame-Options" value="SAMEORIGIN" />
                    <add name="Referrer-Policy" value="strict-origin-when-cross-origin" />
                  </customHeaders>
                </httpProtocol>
              </system.webServer>
            </configuration>
            """, new UTF8Encoding(false), cancellationToken);
    }

    private async Task InitRoundcubeSqliteAsync(string phpExecutable, string applicationRoot, CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(applicationRoot, "temp");
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "roundcube.db").Replace('\\', '/');
        var schemaPath = Path.Combine(applicationRoot, "SQL", "sqlite.initial.sql").Replace('\\', '/');

        var script = $"$pdo = new PDO('sqlite:{databasePath}'); $pdo->exec(file_get_contents('{schemaPath}'));";
        var result = await ProcessRunner.RunAsync(
            phpExecutable,
            $"-r \"{script}\"",
            applicationRoot,
            cancellationToken);
        if (!result.Success)
        {
            throw new AppException("store.install_failed", result.ExitCode, 500, BuildCommandDetails("roundcube sqlite init", result));
        }
    }

    private static async Task ApplyRoundcubeAclAsync(string applicationRoot, string applicationPoolName, CancellationToken cancellationToken)
    {
        var principal = $"IIS AppPool\\{applicationPoolName}";
        foreach (var directory in new[]
                 {
                     Path.Combine(applicationRoot, "temp"),
                     Path.Combine(applicationRoot, "logs")
                 })
        {
            Directory.CreateDirectory(directory);
            var result = await ProcessRunner.RunAsync(
                "icacls.exe",
                $"\"{directory}\" /grant \"{principal}:(OI)(CI)M\" /T /C",
                cancellationToken: cancellationToken);
            if (!result.Success)
            {
                throw new AppException("store.acl_failed", 500, 500, BuildCommandDetails("icacls", result));
            }
        }
    }

    private static string GenerateDesKey()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = RandomNumberGenerator.GetBytes(24);
        var builder = new StringBuilder(24);
        foreach (var value in bytes)
        {
            builder.Append(alphabet[value % alphabet.Length]);
        }

        return builder.ToString();
    }

    private async Task InstallLaravelAsync(
        InstallAppTemplateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SiteName))
        {
            throw new AppException("store.site_required", 400, 400);
        }

        var site = await iisSiteService.GetSiteAsync(request.SiteName.Trim(), cancellationToken);
        var applicationRoot = ResolveApplicationRoot(site);
        EnsureLaravelDestinationReady(applicationRoot, request.CleanInstall);

        var dependencies = await EnsureLaravelDependenciesAsync(cancellationToken);
        var workspace = Path.Combine(dataRoot, "temp", "app-installs", Guid.NewGuid().ToString("N"));
        var stagingRoot = Path.Combine(workspace, "application");
        Directory.CreateDirectory(workspace);
        DeploymentState? deployment = null;

        try
        {
            await RunRequiredCommandAsync(
                dependencies.PhpExecutable,
                $"\"{dependencies.ComposerPath}\" create-project laravel/laravel \"{stagingRoot}\" --no-interaction --prefer-dist --no-dev",
                workspace,
                "composer create-project",
                cancellationToken,
                ComposerEnvironment());

            ValidateLaravelProject(stagingRoot);
            await ConfigureLaravelEnvironmentAsync(stagingRoot, BuildSiteUrl(site), cancellationToken);
            await WriteLaravelWebConfigAsync(Path.Combine(stagingRoot, "public"), cancellationToken);
            await RunRequiredCommandAsync(
                dependencies.PhpExecutable,
                $"\"{dependencies.ComposerPath}\" install --no-interaction --prefer-dist --no-dev --optimize-autoloader",
                stagingRoot,
                "composer install",
                cancellationToken,
                ComposerEnvironment());

            await RunArtisanAsync(dependencies.PhpExecutable, stagingRoot, "key:generate --force", cancellationToken);
            await RunArtisanAsync(dependencies.PhpExecutable, stagingRoot, "migrate --force", cancellationToken);
            await RunArtisanAsync(dependencies.PhpExecutable, stagingRoot, "optimize", cancellationToken);

            deployment = DeployApplication(stagingRoot, applicationRoot, workspace);
            await ApplyLaravelAclAsync(applicationRoot, site.ApplicationPoolName, cancellationToken);

            await phpService.SetSitePhpVersionAsync(
                site.Name,
                new SetSitePhpVersionRequest { ComponentId = dependencies.PhpComponentId },
                cancellationToken);

            var publicRoot = Path.Combine(applicationRoot, "public");
            await iisSiteService.UpdatePhysicalPathAsync(site.Name, publicRoot, cancellationToken);
            await TryCreateStorageLinkAsync(dependencies.PhpExecutable, applicationRoot, cancellationToken);
            await TryEnsureLaravelSchedulerAsync(
                site,
                dependencies.PhpExecutable,
                applicationRoot,
                cancellationToken);
        }
        catch (AppException)
        {
            await RollbackLaravelDeploymentAsync(site, deployment, cancellationToken);
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RollbackLaravelDeploymentAsync(site, deployment, cancellationToken);
            throw new AppException("store.install_failed", 500, 500, BuildDiagnosticDetails(exception));
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    private async Task<LaravelDependencies> EnsureLaravelDependenciesAsync(CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var phpManifest = catalog.Manifest
            .Where(item => item.Id.StartsWith("php-", StringComparison.OrdinalIgnoreCase))
            .Select(item => new { Item = item, Version = ParsePhpVersion(item.Id) })
            .Where(item => item.Version >= new Version(8, 3))
            .OrderByDescending(item => item.Version)
            .Select(item => item.Item)
            .FirstOrDefault()
            ?? throw new AppException("store.php_unsupported", 409, 409);

        var php = FindInstalled(catalog, phpManifest.Id);
        if (php is null)
        {
            await phpService.InstallVersionAsync(phpManifest.Id, null, null, cancellationToken);
            catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
            php = FindInstalled(catalog, phpManifest.Id);
        }
        else
        {
            await phpService.RegisterFastCgiAsync(phpManifest.Id, cancellationToken);
        }

        if (php is null)
        {
            throw new AppException("store.dependencies_failed", 500, 500);
        }

        await ComponentBootstrapper.EnsurePhpIniAsync(php.InstallDir, cancellationToken);

        var composer = FindInstalled(catalog, "composer");
        if (composer is null)
        {
            await componentBootstrapper.InstallAsync("composer", null, null, cancellationToken);
            catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
            composer = FindInstalled(catalog, "composer");
        }

        if (FindInstalled(catalog, "url-rewrite") is null)
        {
            await componentBootstrapper.InstallAsync("url-rewrite", null, null, cancellationToken);
        }

        if (composer is null)
        {
            throw new AppException("store.dependencies_failed", 500, 500);
        }

        var phpExecutable = Path.Combine(php.InstallDir, "php.exe");
        var composerPath = Path.Combine(composer.InstallDir, "composer.phar");
        if (!File.Exists(phpExecutable) || !File.Exists(composerPath))
        {
            throw new AppException("store.dependencies_failed", 500, 500);
        }

        return new LaravelDependencies(php.ComponentId, phpExecutable, composerPath);
    }

    private async Task ConfigureLaravelEnvironmentAsync(
        string applicationRoot,
        string appUrl,
        CancellationToken cancellationToken)
    {
        var environmentPath = Path.Combine(applicationRoot, ".env");
        var examplePath = Path.Combine(applicationRoot, ".env.example");
        if (!File.Exists(environmentPath) && File.Exists(examplePath))
        {
            File.Copy(examplePath, environmentPath);
        }

        if (!File.Exists(environmentPath))
        {
            throw new AppException("store.invalid_laravel_package", 500, 500);
        }

        var sqlitePath = Path.Combine(applicationRoot, "database", "database.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);
        if (!File.Exists(sqlitePath))
        {
            await File.WriteAllBytesAsync(sqlitePath, [], cancellationToken);
        }

        var lines = (await File.ReadAllLinesAsync(environmentPath, cancellationToken)).ToList();
        SetEnvironmentValue(lines, "APP_ENV", "production");
        SetEnvironmentValue(lines, "APP_DEBUG", "false");
        SetEnvironmentValue(lines, "APP_URL", appUrl);
        SetEnvironmentValue(lines, "DB_CONNECTION", "sqlite");
        SetEnvironmentValue(lines, "SESSION_DRIVER", "file");
        SetEnvironmentValue(lines, "CACHE_STORE", "file");
        SetEnvironmentValue(lines, "QUEUE_CONNECTION", "sync");
        await File.WriteAllLinesAsync(environmentPath, lines, new UTF8Encoding(false), cancellationToken);
    }

    private static async Task WriteLaravelWebConfigAsync(string publicRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(publicRoot);
        await File.WriteAllTextAsync(Path.Combine(publicRoot, "web.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <system.webServer>
                <defaultDocument>
                  <files>
                    <clear />
                    <add value="index.php" />
                  </files>
                </defaultDocument>
                <rewrite>
                  <rules>
                    <rule name="Laravel Front Controller" stopProcessing="true">
                      <match url="^(.*)$" ignoreCase="false" />
                      <conditions logicalGrouping="MatchAll">
                        <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
                        <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
                      </conditions>
                      <action type="Rewrite" url="index.php" appendQueryString="true" />
                    </rule>
                  </rules>
                </rewrite>
                <security>
                  <requestFiltering removeServerHeader="true">
                    <hiddenSegments>
                      <add segment=".env" />
                    </hiddenSegments>
                  </requestFiltering>
                </security>
                <httpProtocol>
                  <customHeaders>
                    <remove name="X-Powered-By" />
                    <add name="X-Content-Type-Options" value="nosniff" />
                    <add name="X-Frame-Options" value="SAMEORIGIN" />
                    <add name="Referrer-Policy" value="strict-origin-when-cross-origin" />
                  </customHeaders>
                </httpProtocol>
              </system.webServer>
            </configuration>
            """, new UTF8Encoding(false), cancellationToken);
    }

    private static DeploymentState DeployApplication(string source, string destination, string workspace)
    {
        Directory.CreateDirectory(destination);
        var backup = Path.Combine(workspace, "previous");
        Directory.CreateDirectory(backup);

        foreach (var fileName in ReplaceableStarterFiles)
        {
            var existingPath = Path.Combine(destination, fileName);
            if (!File.Exists(existingPath))
            {
                continue;
            }

            File.Copy(existingPath, Path.Combine(backup, fileName), overwrite: true);
            File.Delete(existingPath);
        }

        var deployedEntries = new DirectoryInfo(source)
            .EnumerateFileSystemInfos()
            .Select(item => item.Name)
            .ToArray();

        try
        {
            CopyDirectory(source, destination);
            return new DeploymentState(destination, backup, deployedEntries);
        }
        catch
        {
            RestoreDeployment(new DeploymentState(destination, backup, deployedEntries));
            throw;
        }
    }

    private async Task RollbackLaravelDeploymentAsync(
        SiteDto site,
        DeploymentState? deployment,
        CancellationToken cancellationToken)
    {
        if (deployment is null)
        {
            return;
        }

        try
        {
            await iisSiteService.UpdatePhysicalPathAsync(site.Name, site.PhysicalPath, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Laravel rollback could not restore IIS path for {SiteName}.", site.Name);
        }

        try
        {
            RestoreDeployment(deployment);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Laravel rollback could not restore site files for {SiteName}.", site.Name);
        }
    }

    private static void RestoreDeployment(DeploymentState state)
    {
        foreach (var entryName in state.DeployedEntries)
        {
            var path = Path.Combine(state.Destination, entryName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        if (!Directory.Exists(state.Backup))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(state.Backup))
        {
            File.Copy(file, Path.Combine(state.Destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static async Task ApplyLaravelAclAsync(
        string applicationRoot,
        string applicationPoolName,
        CancellationToken cancellationToken)
    {
        var principal = $"IIS AppPool\\{applicationPoolName}";
        foreach (var directory in new[]
                 {
                     Path.Combine(applicationRoot, "storage"),
                     Path.Combine(applicationRoot, "bootstrap", "cache"),
                     Path.Combine(applicationRoot, "database")
                 })
        {
            Directory.CreateDirectory(directory);
            var result = await ProcessRunner.RunAsync(
                "icacls.exe",
                $"\"{directory}\" /grant \"{principal}:(OI)(CI)M\" /T /C",
                cancellationToken: cancellationToken);
            if (!result.Success)
            {
                throw new AppException("store.acl_failed", 500, 500, BuildCommandDetails("icacls", result));
            }
        }
    }

    private async Task TryCreateStorageLinkAsync(
        string phpExecutable,
        string applicationRoot,
        CancellationToken cancellationToken)
    {
        var publicStorage = Path.Combine(applicationRoot, "public", "storage");
        if (Directory.Exists(publicStorage) || File.Exists(publicStorage))
        {
            return;
        }

        var result = await ProcessRunner.RunAsync(
            phpExecutable,
            "artisan storage:link",
            applicationRoot,
            cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning(
                "Laravel storage link could not be created. SiteRoot={SiteRoot}, Error={Error}",
                applicationRoot,
                result.StandardError);
        }
    }

    private async Task TryEnsureLaravelSchedulerAsync(
        SiteDto site,
        string phpExecutable,
        string applicationRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var safeSiteName = new string(site.Name
                .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
                .ToArray());
            await schedulerService.CreateTaskAsync(new CreateScheduledTaskRequest
            {
                Name = $"Laravel-{safeSiteName}-Scheduler",
                Command = phpExecutable,
                Arguments = "artisan schedule:run",
                WorkingDirectory = applicationRoot,
                ScheduleType = "minutely",
                Interval = 1
            }, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Laravel scheduler task could not be created for site {SiteName}.",
                site.Name);
        }
    }

    private static Task RunArtisanAsync(
        string phpExecutable,
        string applicationRoot,
        string arguments,
        CancellationToken cancellationToken)
    {
        return RunRequiredCommandAsync(
            phpExecutable,
            $"artisan {arguments}",
            applicationRoot,
            $"php artisan {arguments}",
            cancellationToken);
    }

    private static async Task RunRequiredCommandAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        string operation,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var result = await ProcessRunner.RunAsync(
            fileName,
            arguments,
            workingDirectory,
            cancellationToken,
            environmentVariables);
        if (!result.Success)
        {
            throw new AppException("store.install_failed", result.ExitCode, 500, BuildCommandDetails(operation, result));
        }
    }

    private async Task<string> ResolveDestinationAsync(InstallAppTemplateRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.TargetPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.TargetPath.Trim()));
        }

        if (string.IsNullOrWhiteSpace(request.SiteName))
        {
            throw new AppException("store.target_required", 400, 400);
        }

        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        return ResolveApplicationRoot(site);
    }

    private static string ResolveApplicationRoot(SiteDto site)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(site.ApplicationRootPath)
            ? site.PhysicalPath
            : site.ApplicationRootPath);
    }

    /// <summary>
    /// Hedef klasörü kuruluma hazırlar. Boş değilse: cleanInstall=false → 409 (Existing entries...);
    /// cleanInstall=true → panel "yedekle + temizle": mevcut içeriği kardeş "_yedek_&lt;tarih&gt;" klasörüne
    /// taşır, klasörü boşaltır. Taşındıysa yedek klasör yolunu döner (yoksa null).
    /// </summary>
    private static string? EnsureLaravelDestinationReady(string destination, bool cleanInstall)
    {
        Directory.CreateDirectory(destination);
        var unexpectedEntries = new DirectoryInfo(destination)
            .EnumerateFileSystemInfos()
            .Where(item =>
                !ReplaceableStarterFiles.Contains(item.Name) &&
                !(item is DirectoryInfo &&
                  string.Equals(item.Name, "App_Data", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (unexpectedEntries.Length == 0)
        {
            return null;
        }

        if (!cleanInstall)
        {
            throw new AppException(
                "store.site_has_content",
                409,
                409,
                $"Existing entries: {string.Join(", ", unexpectedEntries.Select(item => item.Name).Take(10))}");
        }

        // Yedekle + temizle: kardeş klasör (destination'ın DIŞINDA) → klasör gerçekten boşalır.
        var backup = $"{destination.TrimEnd('\\', '/')}_yedek_{DateTime.Now:yyyyMMdd_HHmmss}";
        Directory.CreateDirectory(backup);
        foreach (var item in unexpectedEntries)
        {
            var target = Path.Combine(backup, item.Name);
            if (item is DirectoryInfo dir)
            {
                Directory.Move(dir.FullName, target);
            }
            else
            {
                File.Move(item.FullName, target);
            }
        }

        return backup;
    }

    private static void ValidateLaravelProject(string applicationRoot)
    {
        if (!File.Exists(Path.Combine(applicationRoot, "artisan")) ||
            !File.Exists(Path.Combine(applicationRoot, "composer.json")) ||
            !File.Exists(Path.Combine(applicationRoot, "public", "index.php")))
        {
            throw new AppException("store.invalid_laravel_package", 500, 500);
        }
    }

    private static Version ParsePhpVersion(string componentId)
    {
        return Version.TryParse(componentId["php-".Length..], out var version)
            ? version
            : new Version(0, 0);
    }

    private static InstalledComponentDto? FindInstalled(SoftwareCatalogDto catalog, string componentId)
    {
        return catalog.Installed.FirstOrDefault(item =>
            string.Equals(item.ComponentId, componentId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Status, "Installed", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSiteUrl(SiteDto site)
    {
        var binding = site.Bindings
            .OrderByDescending(item => string.Equals(item.Protocol, "https", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Port)
            .FirstOrDefault();
        if (binding is null)
        {
            return "http://localhost";
        }

        var scheme = string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        var host = !string.IsNullOrWhiteSpace(binding.HostName)
            ? binding.HostName
            : binding.IpAddress is "*" or "0.0.0.0" ? "localhost" : binding.IpAddress;
        var defaultPort = scheme == "https" ? 443 : 80;
        return binding.Port == defaultPort ? $"{scheme}://{host}" : $"{scheme}://{host}:{binding.Port}";
    }

    private static void SetEnvironmentValue(List<string> lines, string key, string value)
    {
        var index = lines.FindIndex(line =>
            line.TrimStart().StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase));
        var escapedValue = value.Any(character => char.IsWhiteSpace(character) || character is '#' or '"')
            ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
            : value;
        var setting = $"{key}={escapedValue}";
        if (index >= 0)
        {
            lines[index] = setting;
        }
        else
        {
            lines.Add(setting);
        }
    }

    private static IReadOnlyDictionary<string, string> ComposerEnvironment()
    {
        return new Dictionary<string, string>
        {
            ["COMPOSER_ALLOW_SUPERUSER"] = "1",
            ["COMPOSER_MEMORY_LIMIT"] = "-1"
        };
    }

    private async Task<WordPressPackage> ResolveWordPressPackageAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        // ResponseHeadersRead ile yalnız bağlan+header fazını sınırlar (gövde indirme sınırsız kalır);
        // Infinite'te takılı sunucuya bağlanış sonsuza asılırdı.
        client.Timeout = TimeSpan.FromMinutes(2);
        using var response = await client.GetAsync(
            "https://api.wordpress.org/core/version-check/1.7/?locale=en_US",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        EnsureDownloadSuccess(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("offers", out var offers))
        {
            throw new AppException("store.checksum_unavailable", 502, 502);
        }

        foreach (var offer in offers.EnumerateArray())
        {
            var responseType = offer.TryGetProperty("response", out var responseElement)
                ? responseElement.GetString()
                : string.Empty;
            if (!string.Equals(responseType, "upgrade", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = offer.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : string.Empty;
            var downloadUrl = offer.TryGetProperty("packages", out var packages) &&
                              packages.TryGetProperty("full", out var fullPackage)
                ? fullPackage.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            using var checksumResponse = await client.GetAsync(
                $"{downloadUrl}.sha1",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!checksumResponse.IsSuccessStatusCode)
            {
                throw new AppException("store.checksum_unavailable", 502, 502);
            }

            var checksumText = await checksumResponse.Content.ReadAsStringAsync(cancellationToken);
            var checksumMatch = Regex.Match(checksumText, @"(?i)(?<![0-9a-f])[0-9a-f]{40}(?![0-9a-f])");
            if (!checksumMatch.Success)
            {
                throw new AppException("store.checksum_unavailable", 502, 502);
            }

            return new WordPressPackage(
                version,
                downloadUrl,
                checksumMatch.Value.ToLowerInvariant());
        }

        throw new AppException("store.checksum_unavailable", 502, 502);
    }

    private async Task VerifyWordPressFilesAsync(
        string applicationRoot,
        string version,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        // ResponseHeadersRead ile yalnız bağlan+header fazını sınırlar (gövde indirme sınırsız kalır);
        // Infinite'te takılı sunucuya bağlanış sonsuza asılırdı.
        client.Timeout = TimeSpan.FromMinutes(2);
        using var response = await client.GetAsync(
            $"https://api.wordpress.org/core/checksums/1.0/?version={Uri.EscapeDataString(version)}&locale=en_US",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        EnsureDownloadSuccess(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("checksums", out var checksums) ||
            checksums.ValueKind != JsonValueKind.Object)
        {
            throw new AppException("store.checksum_unavailable", 502, 502);
        }

        var root = Path.GetFullPath(applicationRoot);
        foreach (var checksum in checksums.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = checksum.Name.Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!filePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(filePath))
            {
                throw new AppException("store.checksum_mismatch", 400, 400, $"File={checksum.Name}");
            }

            await using var file = File.OpenRead(filePath);
            var actual = Convert.ToHexString(await MD5.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actual, checksum.Value.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException("store.checksum_mismatch", 400, 400, $"File={checksum.Name}");
            }
        }
    }

    private async Task DownloadAndExtractZipAsync(
        string url,
        string expectedSha1,
        string destination,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        // ResponseHeadersRead ile yalnız bağlan+header fazını sınırlar (gövde indirme sınırsız kalır);
        // Infinite'te takılı sunucuya bağlanış sonsuza asılırdı.
        client.Timeout = TimeSpan.FromMinutes(2);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureDownloadSuccess(response);

        var tempFile = Path.GetTempFileName();
        try
        {
            await using (var stream = File.Create(tempFile))
            {
                await response.Content.CopyToAsync(stream, cancellationToken);
            }

            await using (var stream = File.OpenRead(tempFile))
            {
                var actualSha1 = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AppException("store.checksum_mismatch", 400, 400);
                }
            }

            ZipFile.ExtractToDirectory(tempFile, destination, overwriteFiles: true);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static void EnsureDownloadSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new AppException("store.download_not_found", 404, 404);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AppException("store.download_failed", (int)response.StatusCode, 502);
        }
    }

    private static void FlattenSingleTopDirectory(string destination)
    {
        var root = new DirectoryInfo(destination);
        var directories = root.GetDirectories();
        if (directories.Length != 1 || root.GetFiles().Length > 0)
        {
            return;
        }

        var sourceDirectory = directories[0];
        foreach (var file in sourceDirectory.GetFiles("*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory.FullName, file.FullName);
            var targetPath = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? destination);
            file.MoveTo(targetPath, overwrite: true);
        }

        sourceDirectory.Delete(recursive: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string BuildCommandDetails(string operation, CommandResult result)
    {
        var details = $"""
            Operation={operation}
            ExitCode={result.ExitCode}
            StandardOutput:
            {result.StandardOutput}
            StandardError:
            {result.StandardError}
            """;
        return details.Length <= 8000 ? details : details[..8000] + "\r\n... (diagnostic output truncated)";
    }

    private static string BuildDiagnosticDetails(Exception exception)
    {
        var details = exception.ToString();
        return details.Length <= 8000 ? details : details[..8000] + "\r\n... (diagnostic output truncated)";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed record LaravelDependencies(
        string PhpComponentId,
        string PhpExecutable,
        string ComposerPath);

    private sealed record DeploymentState(
        string Destination,
        string Backup,
        string[] DeployedEntries);

    private sealed record WordPressPackage(
        string Version,
        string DownloadUrl,
        string PackageSha1);
}
