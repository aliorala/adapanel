using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Databases;
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
    IDatabaseService databaseService,
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

    /// <summary>
    /// Uygulama katalogu. <b>Metin alanlari CEVIRI ANAHTARI tasir, cumle TASIMAZ.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// OLCULEN HATA: <c>Description</c> ve <c>AutomaticSetup</c> degerleri burada sabit TURKCE
    /// cumlelerdi ve <c>AppStoreView.vue</c> onlari <c>t()</c>'den GECIRMEDEN basiyordu
    /// (satir 119 ve 134: <c>{{ template.description }}</c>, <c>{{ step }}</c>). Yani dili
    /// Ingilizce olan musteri, panelin en cok bakilan katalog ekraninda "Laravel 13
    /// uygulamasini uretim ayarlari... hazirlar." cumlesini goruyordu. Kategori sutunu
    /// (<c>categoryLabel</c>) bu isi ZATEN dogru yapiyordu; acik olan tam da onun yanindaki
    /// iki sutundu.
    /// </para>
    /// <para>
    /// COZUM PANELIN VAR OLAN YOLU: sunucu anahtar doner, arayuz cevirir. Ceviri bulunamazsa
    /// <c>serverText</c> degeri OLDUGU GIBI basar, yani yeni bir sablon eklenip cevirisi
    /// unutulsa bile ekranda bosluk olusmaz.
    /// </para>
    /// <para>
    /// CEVRILMEYENLER BILINCLIDIR: urun/dosya adlari (<c>SQLite</c>, <c>web.config</c>,
    /// <c>PHP 8.3+</c>, <c>Composer</c>) dilden bagimsizdir; onlari anahtara cevirmek iki
    /// sozluge de aynen kopyalanan olu anahtar uretirdi. Yalniz Turkce baglac iceren
    /// gereksinim ("store.requirement.mysql_or_mariadb") anahtara cevrildi.
    /// </para>
    /// <para>
    /// Anahtarlarda tire YOK, alt cizgi var (<c>store.template.plain_php.description</c>):
    /// arayuzun anahtar tanima kurali (<c>utils/apiError.looksLikeMessageKey</c>) yalnizca
    /// <c>[a-z0-9_]</c> kabul eder; <c>plain-php</c> yazilsaydi anahtar sayilmaz, kullanici ham
    /// anahtari gorurdu. Nobetci: <c>AppStoreCatalogLocalizationTests</c>.
    /// </para>
    /// </remarks>
    private static readonly AppTemplateDto[] Templates =
    [
        new()
        {
            Id = "laravel",
            DisplayName = "Laravel",
            Description = "store.template.laravel.description",
            Runtime = "PHP 8.3+",
            Category = "Framework",
            DownloadUrl = "https://packagist.org/packages/laravel/laravel",
            RequiresDatabase = true,
            DefaultRootFolder = "public",
            Requirements = ["PHP 8.3+", "Composer", "IIS URL Rewrite"],
            AutomaticSetup = ["SQLite", "store.setup.app_key", "store.setup.db_migrations", "store.setup.php_binding", "store.setup.scheduler", "store.setup.folder_permissions"],
            Featured = true
        },
        new()
        {
            Id = "wordpress",
            DisplayName = "WordPress",
            Description = "store.template.wordpress.description",
            Runtime = "PHP",
            Category = "CMS",
            DownloadUrl = "https://wordpress.org/latest.zip",
            RequiresDatabase = true,
            DefaultRootFolder = "wordpress",
            Requirements = ["PHP", "store.requirement.mysql_or_mariadb"],
            AutomaticSetup = ["store.setup.download", "store.setup.extract"]
        },
        new()
        {
            Id = "roundcube",
            DisplayName = "Roundcube Webmail",
            Description = "store.template.roundcube.description",
            Runtime = "PHP 8.x",
            Category = "Mail",
            DownloadUrl = "https://github.com/roundcube/roundcubemail/releases/latest",
            RequiresDatabase = false,
            DefaultRootFolder = "webmail",
            Requirements = ["PHP 8.x", "Stalwart Mail Server (IMAP/SMTP)"],
            AutomaticSetup = ["store.setup.download", "SQLite", "store.setup.config_file", "store.setup.imap_smtp", "store.setup.php_binding", "store.setup.folder_permissions"],
            Featured = true
        },
        new()
        {
            Id = "adminer",
            DisplayName = "Adminer",
            Description = "store.template.adminer.description",
            Runtime = "PHP",
            Category = "Database",
            DownloadUrl = "https://github.com/vrana/adminer/releases/download/v5.4.2/adminer-5.4.2.php",
            RequiresDatabase = false,
            DefaultRootFolder = "adminer",
            Requirements = ["PHP"],
            AutomaticSetup = ["store.setup.single_file"]
        },
        new()
        {
            Id = "grav",
            DisplayName = "Grav CMS",
            Description = "store.template.grav.description",
            Runtime = "PHP 8.x",
            Category = "CMS",
            DownloadUrl = "https://getgrav.org/download/core/grav/latest",
            RequiresDatabase = false,
            DefaultRootFolder = "site koku",
            Requirements = ["PHP 8.x"],
            AutomaticSetup = ["store.setup.download", "store.setup.extract", "store.setup.php_entrypoint"],
            Featured = true
        },
        new()
        {
            Id = "drupal",
            DisplayName = "Drupal",
            Description = "store.template.drupal.description",
            Runtime = "PHP 8.x",
            Category = "CMS",
            DownloadUrl = "https://www.drupal.org/download-latest/zip",
            RequiresDatabase = true,
            DefaultRootFolder = "site koku",
            Requirements = ["PHP 8.x", "store.requirement.mysql_or_mariadb"],
            AutomaticSetup = ["store.setup.download", "store.setup.extract", "store.setup.php_entrypoint"]
        },
        new()
        {
            // Joomla surum-pinli (Adminer emsali): downloads.joomla.org/latest HTML dondugu icin
            // dogrudan zip veren GitHub release asset'i kullanilir (canli dogrulandi: kokte index.php
            // + installation/ sihirbazi, tek-ust-klasor YOK -> flatten no-op).
            Id = "joomla",
            DisplayName = "Joomla",
            Description = "store.template.joomla.description",
            Runtime = "PHP 8.x",
            Category = "CMS",
            DownloadUrl = "https://github.com/joomla/joomla-cms/releases/download/6.1.1/Joomla_6.1.1-Stable-Full_Package.zip",
            RequiresDatabase = true,
            DefaultRootFolder = "site koku",
            Requirements = ["PHP 8.x", "store.requirement.mysql_or_mariadb"],
            AutomaticSetup = ["store.setup.download", "store.setup.extract", "store.setup.php_entrypoint"]
        },
        new()
        {
            // PrestaShop 9.x GitHub'a zip asset koymuyor; son zip'li kararli seri 8.2.x pinlenir
            // (canli dogrulandi: kokte index.php + prestashop.zip -> index.php tarayicida
            // kendini acar ve kurulum sihirbazini baslatir).
            Id = "prestashop",
            DisplayName = "PrestaShop",
            Description = "store.template.prestashop.description",
            Runtime = "PHP 8.x",
            Category = "E-Ticaret",
            DownloadUrl = "https://github.com/PrestaShop/PrestaShop/releases/download/8.2.7/prestashop_8.2.7.zip",
            RequiresDatabase = true,
            DefaultRootFolder = "site koku",
            Requirements = ["PHP 8.x", "store.requirement.mysql_or_mariadb"],
            AutomaticSetup = ["store.setup.download", "store.setup.extract", "store.setup.php_entrypoint"]
        },
        new()
        {
            Id = "plain-php",
            DisplayName = "PHP Starter",
            Description = "store.template.plain_php.description",
            Runtime = "PHP",
            Category = "Starter",
            DownloadUrl = string.Empty,
            RequiresDatabase = false,
            DefaultRootFolder = "public",
            Requirements = ["PHP"],
            AutomaticSetup = ["store.setup.starter_file"]
        },
        new()
        {
            Id = "aspnet-static",
            DisplayName = "ASP.NET Starter",
            Description = "store.template.aspnet_static.description",
            Runtime = "ASP.NET",
            Category = "Starter",
            DownloadUrl = string.Empty,
            RequiresDatabase = false,
            DefaultRootFolder = "site",
            Requirements = ["IIS"],
            AutomaticSetup = ["store.setup.web_config", "store.setup.starter_page"]
        }
    ];

    private readonly string dataRoot = Path.GetFullPath(
        Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));

    public Task<IReadOnlyCollection<AppTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<AppTemplateDto>>(Templates);
    }

    public async Task<AppInstallResultDto> InstallAsync(InstallAppTemplateRequest request, CancellationToken cancellationToken = default)
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

            return new AppInstallResultDto { TemplateId = template.Id };
        }

        if (template.Id == "roundcube")
        {
            await InstallRoundcubeAsync(request, cancellationToken);
            return new AppInstallResultDto { TemplateId = template.Id };
        }

        var destination = await ResolveDestinationAsync(request, cancellationToken);
        Directory.CreateDirectory(destination);

        (string Url, string User, string Password)? wordpressAdmin = null;
        switch (template.Id)
        {
            case "wordpress":
                wordpressAdmin = await InstallWordPressAsync(request, destination, cancellationToken);
                break;
            case "adminer":
                await InstallVerifiedAdminerAsync(destination, cancellationToken);
                break;
            case "grav":
            case "drupal":
            case "joomla":
            case "prestashop":
                await InstallDownloadableAppAsync(template, destination, cancellationToken);
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

        return new AppInstallResultDto
        {
            TemplateId = template.Id,
            AdminUrl = wordpressAdmin?.Url ?? string.Empty,
            AdminUser = wordpressAdmin?.User ?? string.Empty,
            AdminPassword = wordpressAdmin?.Password ?? string.Empty
        };
    }

    // WordPress için gereken/önerilen PHP eklentileri. mysqli OLMADAN WordPress DB'ye HİÇ bağlanamaz (en kritik).
    private static readonly string[] WordPressExtensions =
    {
        "mysqli", "pdo_mysql", "mbstring", "gd", "curl", "exif", "intl", "zip", "openssl", "fileinfo"
    };

    // Genel indirilebilir PHP uygulaması (Grav, Drupal ...): resmi zip'i geçici alana indir+aç, tek üst
    // klasörü düzleştir, site köküne kopyala. WordPress ile aynı kanıtlanmış akış (workspace→flatten→copy).
    private async Task InstallDownloadableAppAsync(AppTemplateDto template, string destination, CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(dataRoot, "temp", "app-installs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await DownloadAndExtractZipAsync(template.DownloadUrl, string.Empty, workspace, cancellationToken);
            FlattenSingleTopDirectory(workspace);
            CopyDirectory(workspace, destination);
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    private async Task<(string Url, string User, string Password)?> InstallWordPressAsync(InstallAppTemplateRequest request, string destination, CancellationToken cancellationToken)
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

        // TURNKEY (Plesk/cPanel WordPress Toolkit gibi): PHP'de mysqli vb. etkinleştir + veritabanı/kullanıcı oluştur +
        // wp-config.php'yi yaz → kullanıcı DB ekranını ELLE doldurmak zorunda kalmaz, doğrudan "5 dakikalık" kuruluma düşer.
        // Best-effort: DB sağlanamasa bile dosyalar kurulu kalır (kullanıcı wp-config'i elle tamamlayabilir).
        try
        {
            var (phpComponentId, phpExecutable) = await EnsurePhpRuntimeAsync(cancellationToken);
            await phpService.EnsureExtensionsAsync(phpComponentId, WordPressExtensions, cancellationToken);

            var (dbName, dbUser, dbPassword) = await ProvisionWordPressDatabaseAsync(request, cancellationToken);
            WriteWordPressConfig(destination, dbName, dbUser, dbPassword);
            logger.LogInformation("WordPress veritabanı sağlandı (site {Site}): db={Db} user={User}", request.SiteName, dbName, dbUser);

            // wp-cli ile çekirdek kurulumu (admin hesabı oluştur) → site DB ekranı YERİNE doğrudan giriş yapılabilir olur.
            return await TryInstallWordPressCoreAsync(request, destination, phpExecutable, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "WordPress DB/wp-config otomatik kurulamadı (dosyalar kuruldu): {Site}", request.SiteName);
            return null;
        }
    }

    // wp-cli ile WordPress çekirdek kurulumunu tamamlar (admin hesabı + site başlığı). Başarılıysa giriş bilgilerini döner;
    // başarısızsa (ör. zaten kurulu) null → kullanıcı WP'nin "5 dakikalık" ekranını kullanır. PHP CLI + DB + wp-config gerekir.
    private async Task<(string Url, string User, string Password)?> TryInstallWordPressCoreAsync(
        InstallAppTemplateRequest request,
        string destination,
        string phpExecutable,
        CancellationToken cancellationToken)
    {
        try
        {
            var wpCli = await EnsureWpCliAsync(cancellationToken);
            var (url, title) = await ResolveSiteUrlAndTitleAsync(request, cancellationToken);
            var host = new Uri(url).Host;
            const string adminUser = "admin";
            var adminPassword = "Wp!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var adminEmail = host.Contains('.') ? $"admin@{host}" : "admin@example.com";

            var arguments =
                $"\"{wpCli}\" --path=\"{destination}\" core install " +
                $"--url=\"{url}\" --title=\"{title}\" --admin_user=\"{adminUser}\" " +
                $"--admin_password=\"{adminPassword}\" --admin_email=\"{adminEmail}\" --skip-email";

            var result = await ProcessRunner.RunAsync(
                phpExecutable,
                arguments,
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromMinutes(5));

            if (result.ExitCode != 0)
            {
                logger.LogWarning("wp core install başarısız ({Site}): {Error}", request.SiteName, result.StandardError);
                return null;
            }

            return ($"{url.TrimEnd('/')}/wp-admin/", adminUser, adminPassword);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "wp-cli core install hatası ({Site})", request.SiteName);
            return null;
        }
    }

    // wp-cli.phar'ı indirir (yoksa) ve yolunu döner. Tool dizininde önbelleklenir.
    private async Task<string> EnsureWpCliAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(dataRoot, "tools", "wp-cli");
        Directory.CreateDirectory(directory);
        var phar = Path.Combine(directory, "wp-cli.phar");
        if (File.Exists(phar) && new FileInfo(phar).Length > 1_000_000)
        {
            return phar;
        }

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        var bytes = await client.GetByteArrayAsync(
            "https://raw.githubusercontent.com/wp-cli/builds/gh-pages/phar/wp-cli.phar",
            cancellationToken);
        await File.WriteAllBytesAsync(phar, bytes, cancellationToken);
        return phar;
    }

    // Sitenin URL'sini (ilk host-header binding'inden) ve başlığını çözer. Host yoksa site adına/localhost'a düşer.
    private async Task<(string Url, string Title)> ResolveSiteUrlAndTitleAsync(InstallAppTemplateRequest request, CancellationToken cancellationToken)
    {
        var host = string.Empty;
        try
        {
            var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
            host = site.Bindings
                .Select(binding => binding.HostName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty;
        }
        catch
        {
            // site okunamazsa aşağıda fallback
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            host = string.IsNullOrWhiteSpace(request.SiteName) ? "localhost" : request.SiteName.Trim();
        }

        return ($"http://{host}", host);
    }

    // WordPress için yerel MySQL/MariaDB veritabanı + kullanıcı oluşturur (kullanıcı bu DB'ye tam yetki alır, site'ye
    // bağlanır → site silinince cascade gider). Ad çakışırsa kısa rastgele son ekle benzersizleştirir.
    private async Task<(string Db, string User, string Password)> ProvisionWordPressDatabaseAsync(
        InstallAppTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var engine = await ResolveWordPressDbEngineAsync(cancellationToken);
        var basis = ResourceNaming.DatabaseNameFromSite(
            string.IsNullOrWhiteSpace(request.SiteName) ? "wordpress" : request.SiteName);
        if (string.IsNullOrWhiteSpace(basis))
        {
            basis = "wordpress";
        }

        var password = "Wp1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var dbName = basis;
        var dbUser = (basis.Length > 26 ? basis[..26] : basis) + "_u";

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await databaseService.CreateDatabaseAsync(
                    new CreateDatabaseRequest
                    {
                        Engine = engine,
                        Name = dbName,
                        UserName = dbUser,
                        Password = password,
                        SiteName = request.SiteName
                    },
                    cancellationToken);
                return (dbName, dbUser, password);
            }
            catch (AppException exception) when (exception.MessageKey == "database.database_already_exists" && attempt < 5)
            {
                var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();
                dbName = (basis.Length > 24 ? basis[..24] : basis) + "_" + suffix;
                dbUser = (basis.Length > 22 ? basis[..22] : basis) + "_" + suffix + "u";
            }
        }
    }

    // Kurulu MySQL/MariaDB motorunu seç (WordPress için). Yoksa "mariadb" (panel varsayılanı; yoksa CreateDatabaseAsync net hata verir).
    private async Task<string> ResolveWordPressDbEngineAsync(CancellationToken cancellationToken)
    {
        try
        {
            var engines = await databaseService.GetEnginesAsync(cancellationToken);
            var installed = engines.Where(item => item.Installed).Select(item => item.Engine).ToList();
            if (installed.Any(item => string.Equals(item, "mariadb", StringComparison.OrdinalIgnoreCase)))
            {
                return "mariadb";
            }
            if (installed.Any(item => string.Equals(item, "mysql", StringComparison.OrdinalIgnoreCase)))
            {
                return "mysql";
            }
        }
        catch
        {
            // motor listesi alınamazsa varsayılana düş
        }
        return "mariadb";
    }

    // wp-config-sample.php'yi temel alıp DB bilgilerini + benzersiz salt'ları doldurur (yoksa sıfırdan minimal config yazar).
    // DB_HOST = 127.0.0.1 (Windows'ta "localhost" named-pipe'a kayabilir → IP daha güvenli).
    private static void WriteWordPressConfig(string destination, string dbName, string dbUser, string dbPassword)
    {
        var configPath = Path.Combine(destination, "wp-config.php");
        if (File.Exists(configPath))
        {
            return; // kullanıcının/önceki kurulumun config'ini ezme
        }

        var samplePath = Path.Combine(destination, "wp-config-sample.php");
        string content;
        if (File.Exists(samplePath))
        {
            content = File.ReadAllText(samplePath);
            content = content
                .Replace("database_name_here", dbName)
                .Replace("username_here", dbUser)
                .Replace("password_here", dbPassword.Replace("'", "\\'"));
            content = Regex.Replace(content, @"(define\(\s*'DB_HOST',\s*')[^']*('\s*\)\s*;)", "${1}127.0.0.1${2}");
            // 8 adet benzersiz salt: sample'daki 'put your unique phrase here' yer tutucularının HER BİRİ ayrı değerle.
            content = Regex.Replace(content, "put your unique phrase here", _ => GenerateWpSalt());
        }
        else
        {
            content =
                "<?php\n" +
                $"define('DB_NAME', '{dbName}');\n" +
                $"define('DB_USER', '{dbUser}');\n" +
                $"define('DB_PASSWORD', '{dbPassword.Replace("'", "\\'")}');\n" +
                "define('DB_HOST', '127.0.0.1');\n" +
                "define('DB_CHARSET', 'utf8mb4');\n" +
                "define('DB_COLLATE', '');\n" +
                $"define('AUTH_KEY', '{GenerateWpSalt()}');\n" +
                $"define('SECURE_AUTH_KEY', '{GenerateWpSalt()}');\n" +
                $"define('LOGGED_IN_KEY', '{GenerateWpSalt()}');\n" +
                $"define('NONCE_KEY', '{GenerateWpSalt()}');\n" +
                $"define('AUTH_SALT', '{GenerateWpSalt()}');\n" +
                $"define('SECURE_AUTH_SALT', '{GenerateWpSalt()}');\n" +
                $"define('LOGGED_IN_SALT', '{GenerateWpSalt()}');\n" +
                $"define('NONCE_SALT', '{GenerateWpSalt()}');\n" +
                "$table_prefix = 'wp_';\n" +
                "if (!defined('ABSPATH')) { define('ABSPATH', __DIR__ . '/'); }\n" +
                "require_once ABSPATH . 'wp-settings.php';\n";
        }

        File.WriteAllText(configPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // wp-config salt'ı: 64 karakter, SQL/PHP single-quote güvenli (tek tırnak ve ters bölü HARİÇ) alfabe.
    private static string GenerateWpSalt()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()-_=+[]{}<>?,.:;";
        var bytes = RandomNumberGenerator.GetBytes(64);
        var builder = new StringBuilder(64);
        foreach (var value in bytes)
        {
            builder.Append(alphabet[value % alphabet.Length]);
        }
        return builder.ToString();
    }

    private async Task InstallVerifiedAdminerAsync(string destination, CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var installed = FindInstalled(catalog, "adminer");
        if (installed is null)
        {
            await componentBootstrapper.InstallAsync("adminer", null, null, force: false, cancellationToken: cancellationToken);
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
        var prepared = EnsureLaravelDestinationReady(applicationRoot, request.CleanInstall);

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

            // web.config BELGE KÖKÜNE yazılır, uygulama köküne değil: IIS yalnız belge kökündeki
            // (ve altındaki) web.config'i okur. Roundcube'da belge kökü public_html olduğu için
            // uygulama köküne yazılan dosya HİÇ okunmazdı.
            await WriteRoundcubeWebConfigAsync(ResolveRoundcubeDocumentRoot(stagingRoot), cancellationToken);

            deployment = DeployApplication(stagingRoot, applicationRoot, prepared);
            await InitRoundcubeSqliteAsync(php.PhpExecutable, applicationRoot, cancellationToken);
            await ApplyRoundcubeAclAsync(applicationRoot, site.ApplicationPoolName, cancellationToken);
            await phpService.SetSitePhpVersionAsync(
                site.Name,
                new SetSitePhpVersionRequest { ComponentId = php.ComponentId },
                cancellationToken);

            // SİTEYİ GERÇEK BELGE KÖKÜNE YÖNLENDİR — Laravel yolunda yapılan şeyin aynısı.
            // Yapılmadığında kurulum "başarılı" diyor ama site AÇILMIYORDU (aşağıdaki
            // doğrulamanın gerekçesine bakınız).
            var documentRoot = ResolveRoundcubeDocumentRoot(applicationRoot);
            if (!PathsEqual(documentRoot, applicationRoot))
            {
                await iisSiteService.UpdatePhysicalPathAsync(site.Name, documentRoot, cancellationToken);
            }

            await VerifyRoundcubeIsServedAsync(site.Name, documentRoot, cancellationToken);
        }
        catch (AppException)
        {
            await RollbackLaravelDeploymentAsync(site, deployment, prepared, applicationRoot, cancellationToken);
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RollbackLaravelDeploymentAsync(site, deployment, prepared, applicationRoot, cancellationToken);
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

    /// <summary>
    /// Roundcube'un GERÇEK belge kökü: <c>public_html</c> varsa o, yoksa uygulama kökü.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu bir tercih değil, paketin şartı. Panelin bildirimde sabitlediği sürümün
    /// (<c>roundcubemail-1.7.1-complete</c>) ÜST DÜZEY <c>index.php</c> dosyasının TAMAMI şudur:
    /// <c>exit('Please, configure your HTTP server to point to the /public_html directory …')</c>.
    /// Yani site kökü uygulama köküne bakıyorsa Roundcube HİÇ AÇILMAZ; ziyaretçi yalnız bu
    /// cümleyi görür. (Panelin kendi webmail kurulumu bunu zaten biliyor ve doğru yapıyor.)
    /// </para>
    /// <para>
    /// Klasör VARSAYILMAZ, ölçülür: <c>public_html/index.php</c> yoksa (başka bir sürüm/paket)
    /// uygulama kökü döner. Var olmayan bir klasörü belge kökü yapmak siteyi büsbütün kırardı.
    /// </para>
    /// </remarks>
    internal static string ResolveRoundcubeDocumentRoot(string applicationRoot)
    {
        var aday = Path.Combine(applicationRoot, "public_html");
        return File.Exists(Path.Combine(aday, "index.php")) ? aday : applicationRoot;
    }

    /// <summary>İki dosya yolu aynı yeri mi gösteriyor (Windows: büyük/küçük ve sondaki ayraç önemsiz).</summary>
    internal static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// KURULUMDAN SONRA GERİ OKUMA: site gerçekten Roundcube'u mu sunuyor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eskiden bu adım hiç yoktu: kurulum indiriyor, açıyor, kopyalıyor ve "tamam" diyordu.
    /// Sonuç, kullanıcının gördüğü şeyle panelin söylediği şeyin ayrışmasıydı — panel "kuruldu"
    /// diyor, site ise Roundcube'un "HTTP sunucunuzu /public_html'e yönlendirin" uyarısını
    /// gösteriyordu.
    /// </para>
    /// <para>
    /// İki bağımsız gerçek okunur: (1) IIS'in siteye YAZDIĞI fiziksel yol (yazma sessizce
    /// uygulanmamış olabilir), (2) o yoldaki giriş dosyasının Roundcube'un yönlendirme uyarısı
    /// OLMAMASI. İkisi de dosya/IIS düzeyinde ölçülebilir; HTTP isteğine gerek yok — site
    /// bağlaması/host başlığı henüz hazır olmayabilir ve o zaman "ölçemedim"i "bozuk" sanırdık.
    /// </para>
    /// </remarks>
    private async Task VerifyRoundcubeIsServedAsync(string siteName, string documentRoot, CancellationToken cancellationToken)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        if (!PathsEqual(site.PhysicalPath, documentRoot))
        {
            throw new AppException("store.install_failed", 500, 500,
                $"Site fiziksel yolu belge köküne ayarlanamadı. Beklenen='{documentRoot}', okunan='{site.PhysicalPath}'.");
        }

        var girisDosyasi = Path.Combine(documentRoot, "index.php");
        if (!File.Exists(girisDosyasi))
        {
            throw new AppException("store.install_failed", 500, 500,
                $"Belge kökünde index.php yok: '{girisDosyasi}'.");
        }

        var icerik = await File.ReadAllTextAsync(girisDosyasi, cancellationToken);
        if (icerik.Contains(RoundcubeYonlendirmeUyarisi, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("store.install_failed", 500, 500,
                "Site hâlâ Roundcube'un 'sunucunuzu /public_html'e yönlendirin' uyarı sayfasını sunuyor.");
        }
    }

    /// <summary>
    /// Roundcube'un üst düzey <c>index.php</c>'sindeki yönlendirme uyarısının değişmeyen parçası.
    /// </summary>
    /// <remarks>
    /// Uydurulmadı: bildirimde sabitlenen sürümün kaynağından okundu
    /// (<c>roundcubemail 1.7.1 · index.php</c>, tek satır <c>exit(...)</c>). Bu metin görünüyorsa
    /// belge kökü YANLIŞ yerdedir — kurulum "başarılı" demeden önce bunu görmek zorundadır.
    /// </remarks>
    private const string RoundcubeYonlendirmeUyarisi = "point to the /public_html";

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

        // KİRACI YOLU PHP KAYNAĞINA GİRİYORDU.
        //
        // Eskiden bu iki yol, php.exe'ye `-r` ile verilen PHP KAYNAK METNİNİN içindeki tek tırnaklı
        // dizelere KAÇIŞSIZ gömülüyordu. Yol kiracıdan türüyor: `applicationRoot` sitenin fiziksel yoludur
        // ve `NormalizeSiteName`/`PhysicalPath` doğrulaması karakter kümesini SINIRLAMIYOR
        // (WebAdministrationSiteService.NormalizeSiteName yalnız Trim + aygıt adı denetimi yapar).
        // NTFS'te tek tırnak meşru bir yol karakteridir; yani fiziksel yolunda `'` olan bir site,
        // PHP tek tırnaklı dizesini kapatıp kendi PHP ifadesini ekleyebiliyordu. Bu php.exe
        // LocalSystem olarak koşuyor (LimitedIdentityRunner DEĞİL).
        //
        // Kök neden komut satırında kaçış yapmamak değil, YOLU KOD OLARAK GEÇİRMEKTİ. Betik artık
        // panelin kendi geçici klasörüne yazılıp dosyadan çalıştırılıyor ve yollar PHP'nin kendi
        // tek tırnak kuralına göre kaçırılıyor (aynı kalıp: WordPressToolkitService.TryReplaceDefine).
        var betikYolu = Path.Combine(Path.GetTempPath(), $"ada-rc-init-{Guid.NewGuid():N}.php");
        try
        {
            await File.WriteAllTextAsync(
                betikYolu,
                "<?php\n" +
                $"$pdo = new PDO('sqlite:' . {PhpTekTirnak(databasePath)});\n" +
                $"$pdo->exec(file_get_contents({PhpTekTirnak(schemaPath)}));\n",
                new UTF8Encoding(false),
                cancellationToken);

            var result = await ProcessRunner.RunAsync(
                phpExecutable,
                $"\"{betikYolu}\"",
                applicationRoot,
                cancellationToken);
            if (!result.Success)
            {
                throw new AppException("store.install_failed", result.ExitCode, 500, BuildCommandDetails("roundcube sqlite init", result));
            }
        }
        finally
        {
            try { File.Delete(betikYolu); } catch { /* en iyi çaba */ }
        }
    }

    /// <summary>
    /// Değeri PHP TEK TIRNAKLI dize literaline çevirir. PHP'nin tek tırnaklı dizesinde yalnız iki
    /// kaçış vardır: <c>\\</c> ve <c>\'</c>. Değişken genişlemesi YOKTUR — çift tırnaklı dize
    /// kullanılsaydı yoldaki <c>$</c> de ayrı bir kapı olurdu.
    /// </summary>
    internal static string PhpTekTirnak(string value)
        => "'" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'") + "'";

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
        var prepared = EnsureLaravelDestinationReady(applicationRoot, request.CleanInstall);

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

            deployment = DeployApplication(stagingRoot, applicationRoot, prepared);
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
            await RollbackLaravelDeploymentAsync(site, deployment, prepared, applicationRoot, cancellationToken);
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RollbackLaravelDeploymentAsync(site, deployment, prepared, applicationRoot, cancellationToken);
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
            await componentBootstrapper.InstallAsync("composer", null, null, force: false, cancellationToken: cancellationToken);
            catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
            composer = FindInstalled(catalog, "composer");
        }

        if (FindInstalled(catalog, "url-rewrite") is null)
        {
            await componentBootstrapper.InstallAsync("url-rewrite", null, null, force: false, cancellationToken: cancellationToken);
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

    /// <summary>
    /// Hazırlanan uygulamayı sitenin CANLI köküne yerleştirir.
    /// <para>
    /// ÖNCEKİ HÂLDEKİ VERİ KAYBI: yalnızca 5 sabit DOSYA adı (<see cref="ReplaceableStarterFiles"/>)
    /// yedeğe alınıyordu, ama geri alma arşivin HER üst düzey girdisini hedefte siliyordu
    /// (<c>Directory.Delete(recursive: true)</c>) ve geri koyarken yalnızca üst düzey DOSYALARI
    /// kopyalıyordu. Yedekte olmayan bir klasör (müşterinin <c>App_Data</c>'sı ya da adı pakettekiyle
    /// çakışan herhangi bir klasörü) kurulum sonraki adımlardan birinde patladığında kökünden
    /// siliniyor ve GERİ GELMİYORDU.
    /// </para>
    /// <para>
    /// KURAL: üzerine yazılacak HER üst düzey girdi (klasör dahil) önce yana alınır; taşıma
    /// başarısızsa kurulum HİÇ BAŞLAMAZ. Böylece geri alma yalnızca BİZİM oluşturduğumuz girdileri
    /// siler ve yana alınan her şey — dosya ya da klasör — yerine konabilir.
    /// </para>
    /// </summary>
    /// <param name="prepared">
    /// <see cref="EnsureLaravelDestinationReady"/> zaten yana almışsa onun sonucu. Aynı geri alma
    /// adımına ve AYNI kardeş klasöre bağlanır; aksi halde müşterinin içeriği orada ÖKSÜZ kalır
    /// (eski davranış: dönüş değeri iki çağıranda da atılıyordu, kurulum patlayınca site boş kalıyordu).
    /// </param>
    internal static DeploymentState DeployApplication(
        string source,
        string destination,
        DestinationPreparation? prepared = null)
    {
        Directory.CreateDirectory(destination);

        var sourceEntries = new DirectoryInfo(source)
            .EnumerateFileSystemInfos()
            .Select(item => item.Name)
            .ToArray();
        var collisions = new HashSet<string>(sourceEntries, StringComparer.OrdinalIgnoreCase);

        // Yana alma yeri KARDEŞ klasördür, çalışma alanı (workspace) DEĞİL. İki sebep:
        // 1) workspace kurulum sonunda silinir; müşteri içeriğinin ömrü ona bağlanamaz,
        // 2) workspace veri kökünde, site kökü başka bir birimde olabilir — Directory.Move
        //    birimler arasında çalışmaz.
        var sideline = prepared?.SidelineDirectory ?? ContentSideline.BuildSiblingPath(destination, "onceki");
        IReadOnlyList<SidelinedEntry> movedAside;
        try
        {
            movedAside = ContentSideline.MoveAside(
                destination,
                sideline,
                // Paketle çakışan her şey + yerine yenisi gelecek başlangıç dosyaları.
                item => collisions.Contains(item.Name) || ReplaceableStarterFiles.Contains(item.Name));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Veriyi koruyamıyorsak KOPYALAMAYA BAŞLAMAYIZ. Aksi halde müşterinin klasörü hem
            // yerinde kalır hem paket dosyalarıyla karışır; geri alma da onu silmek zorunda kalırdı.
            throw new AppException("store.preserve_failed", 500, 500, $"Destination={destination}\r\n{exception.Message}");
        }

        var state = new DeploymentState(
            destination,
            sideline,
            sourceEntries,
            [.. prepared?.Sidelined ?? [], .. movedAside]);

        try
        {
            CopyDirectory(source, destination);
            return state;
        }
        catch
        {
            RestoreDeployment(state);
            throw;
        }
    }

    /// <summary>
    /// Kurulumu geri alır.
    /// <para>
    /// <paramref name="prepared"/> ayrıca verilir çünkü kurulum <see cref="DeployApplication"/>'a
    /// GELMEDEN de patlayabilir (composer, indirme, doğrulama). O anda müşterinin içeriği çoktan
    /// kardeş klasöre taşınmıştır; yalnızca <paramref name="deployment"/>'a bakan bir geri alma
    /// siteyi BOŞ bırakırdı.
    /// </para>
    /// </summary>
    private async Task RollbackLaravelDeploymentAsync(
        SiteDto site,
        DeploymentState? deployment,
        DestinationPreparation? prepared,
        string applicationRoot,
        CancellationToken cancellationToken)
    {
        if (deployment is null && (prepared is null || prepared.Sidelined.Count == 0))
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
            // Yerleştirmeye gelindiyse DeploymentState hazırlığı ZATEN kapsar (birleşik liste);
            // gelinmediyse yalnız hazırlık aşamasında yana alınanlar geri konur.
            RestoreDeployment(
                deployment ?? new DeploymentState(applicationRoot, prepared!.SidelineDirectory, [], prepared.Sidelined),
                logger);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Laravel rollback could not restore site files for {SiteName}.", site.Name);
        }
    }

    /// <summary>
    /// Kurulumu geri alır: BİZİM kopyaladığımız girdiler silinir, yana alınan müşteri içeriği
    /// (dosya VE klasör) yerine konur. Yerine konamayan bir girdi silinmez; nerede durduğu loglanır.
    /// </summary>
    private static void RestoreDeployment(DeploymentState state, ILogger? logger = null)
    {
        // Yalnızca bu kurulumun oluşturduğu girdiler silinir: çakışan her şey zaten yana alındığı
        // için burada müşterinin klasörü kalmış olamaz.
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

        ContentSideline.MoveBack(
            state.Destination,
            state.Sidelined,
            (entry, exception) => logger?.LogError(
                exception,
                "'{Entry}' geri konamadi. VERI KAYBOLMADI, surada duruyor: {Path}",
                entry.Name,
                entry.SidelinePath));

        // Her şey yerine konduysa kardeş klasörü ortalıkta bırakma; bir şey kaldıysa DOKUNMA.
        ContentSideline.RemoveIfEmpty(state.SidelineDirectory);
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
    /// cleanInstall=true → panel "yedekle + temizle": mevcut içeriği kardeş "_yedek_&lt;tarih&gt;"
    /// klasörüne taşır, klasörü boşaltır.
    /// <para>
    /// Taşınanların LİSTESİ döner ve <see cref="DeployApplication"/> üzerinden geri alma adımına
    /// bağlanır. Eskiden yalnızca klasör yolu dönüyor, iki çağıran da bunu ATIYORDU: kurulum
    /// sonraki adımlardan birinde patlayınca müşterinin bütün içeriği kardeş klasörde öksüz kalıyor,
    /// site boş kalıyor ve kimse nereye gittiğini söylemiyordu.
    /// </para>
    /// </summary>
    internal static DestinationPreparation EnsureLaravelDestinationReady(string destination, bool cleanInstall)
    {
        Directory.CreateDirectory(destination);
        var unexpectedEntries = new DirectoryInfo(destination)
            .EnumerateFileSystemInfos()
            .Where(item =>
                !ReplaceableStarterFiles.Contains(item.Name) &&
                !(item is DirectoryInfo &&
                  string.Equals(item.Name, "App_Data", StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Name)
            .ToArray();

        if (unexpectedEntries.Length == 0)
        {
            return DestinationPreparation.None;
        }

        if (!cleanInstall)
        {
            throw new AppException(
                "store.site_has_content",
                409,
                409,
                $"Existing entries: {string.Join(", ", unexpectedEntries.Take(10))}");
        }

        // Yedekle + temizle: kardeş klasör (destination'ın DIŞINDA) → klasör gerçekten boşalır.
        var backup = ContentSideline.BuildSiblingPath(destination, "yedek");
        var wanted = new HashSet<string>(unexpectedEntries, StringComparer.OrdinalIgnoreCase);
        try
        {
            var moved = ContentSideline.MoveAside(destination, backup, item => wanted.Contains(item.Name));
            return new DestinationPreparation(backup, moved);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Veriyi koruyamıyorsak kuruluma BAŞLAMAYIZ. Yarım taşınmış bir kökün üzerine kurmak,
            // müşterinin dosyalarını kurtarılamaz biçimde karıştırmak demektir.
            throw new AppException("store.preserve_failed", 500, 500, $"Destination={destination}\r\n{exception.Message}");
        }
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
                // Sabit sürüm için SHA1 doğrulanır; "latest" sürükleyen resmi kaynaklarda (sürüm değiştikçe hash
                // değişir) sha boş bırakılır → yalnız HTTPS/resmi kaynağa güvenilir (components.json'daki boş-sha
                // bileşen kurulumlarıyla aynı davranış).
                if (!string.IsNullOrWhiteSpace(expectedSha1) && !string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Hedef klasör kuruluma hazırlanırken yana alınanlar.</summary>
    /// <param name="SidelineDirectory">Yana alınanların durduğu kardeş klasör (yoksa null).</param>
    /// <param name="Sidelined">Taşınan üst düzey girdiler; geri alma bunları yerine koyar.</param>
    internal sealed record DestinationPreparation(
        string? SidelineDirectory,
        IReadOnlyList<SidelinedEntry> Sidelined)
    {
        public static DestinationPreparation None { get; } = new(null, []);
    }

    /// <param name="DeployedEntries">Kopyaladığımız üst düzey girdiler; geri alma YALNIZ bunları siler.</param>
    /// <param name="Sidelined">Üzerine yazmadan önce yana alınan müşteri içeriği; geri alma bunları geri taşır.</param>
    internal sealed record DeploymentState(
        string Destination,
        string? SidelineDirectory,
        string[] DeployedEntries,
        IReadOnlyList<SidelinedEntry> Sidelined);

    private sealed record WordPressPackage(
        string Version,
        string DownloadUrl,
        string PackageSha1);
}
