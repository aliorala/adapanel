using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.AppStore;

/// <summary>
/// panel WordPress Toolkit'in temel, dosya tabanlı karşılığı. WP-CLI gerektirmez:
/// wp-config.php varlığına göre algılar, wp-includes/version.php'den sürümü okur,
/// bakım modunu .maintenance dosyasıyla yönetir.
/// </summary>
public sealed partial class WordPressToolkitService(
    IIisSiteService iisSiteService,
    IDatabaseService databaseService,
    ISiteHostingSettingsService hostingSettings) : IWordPressToolkitService
{
    public async Task<WordPressSiteDto> DetectAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        var latest = await GetLatestCoreVersionAsync(cancellationToken);
        var dto = BuildDto(site.Name, root, site.Bindings.FirstOrDefault()?.HostName, latest);
        // Tek site görünümünde (pano) etkin eklenti/tema durumunu wp-cli ile zenginleştir (best-effort; DetectAll'da YAPMA).
        if (dto.IsWordPress && (dto.Plugins.Count > 0 || dto.Themes.Count > 0))
        {
            await EnrichActiveStateAsync(siteName, dto, cancellationToken);
        }
        return dto;
    }

    public async Task<IReadOnlyCollection<WordPressSiteDto>> DetectAllAsync(CancellationToken cancellationToken = default)
    {
        var sites = await iisSiteService.GetSitesAsync(cancellationToken);
        var latest = await GetLatestCoreVersionAsync(cancellationToken);
        var results = new List<WordPressSiteDto>();
        foreach (var site in sites)
        {
            var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
            var dto = BuildDto(site.Name, root, site.Bindings.FirstOrDefault()?.HostName, latest);
            if (dto.IsWordPress)
            {
                results.Add(dto);
            }
        }

        return results;
    }

    public async Task<WordPressSiteDto> SetMaintenanceAsync(string siteName, bool enabled, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        var maintenanceFile = Path.Combine(root, ".maintenance");

        if (enabled)
        {
            await File.WriteAllTextAsync(maintenanceFile, "<?php $upgrading = time(); ?>", cancellationToken);
        }
        else if (File.Exists(maintenanceFile))
        {
            File.Delete(maintenanceFile);
        }

        var latest = await GetLatestCoreVersionAsync(cancellationToken);
        return BuildDto(site.Name, root, site.Bindings.FirstOrDefault()?.HostName, latest);
    }

    // panel "Giriş yap" karşılığı: geçici, kendini silen bir mu-plugin yazar; yöneticiyi tek tıkla içeri alır.
    // Jeton (token) yalnızca URL'de gider; diskte sadece SHA256 özeti + 2 dk son kullanım tutulur; tek kullanımlık.
    public async Task<WordPressLoginLinkDto> CreateLoginLinkAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "wp-config.php")))
        {
            throw new AppException("wpcli.not_wordpress", 409, 409);
        }

        const int lifetimeSeconds = 120;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var expiresUnix = DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds).ToUnixTimeSeconds();

        var muDir = Path.Combine(root, "wp-content", "mu-plugins");
        Directory.CreateDirectory(muDir);
        await File.WriteAllTextAsync(Path.Combine(muDir, "adapanel-login.php"), BuildLoginPlugin(hash, expiresUnix), cancellationToken);

        // En uygun bağlama: önce https barındırma, sonra herhangi bir host adı, sonra ilk bağlama.
        var bindings = site.Bindings;
        var binding = bindings.FirstOrDefault(b => string.Equals(b.Protocol, "https", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(b.HostName))
                   ?? bindings.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.HostName))
                   ?? bindings.FirstOrDefault();
        var isHttps = binding is not null && string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase);
        var scheme = isHttps ? "https" : "http";
        var host = string.IsNullOrWhiteSpace(binding?.HostName) ? "localhost" : binding!.HostName;
        var port = binding?.Port ?? 0;
        var portPart = port == 0 || (isHttps && port == 443) || (!isHttps && port == 80) ? string.Empty : $":{port}";

        return new WordPressLoginLinkDto
        {
            Url = $"{scheme}://{host}{portPart}/?adapanel_sso={token}",
            ExpiresInSeconds = lifetimeSeconds
        };
    }

    // Tek kullanımlık giriş eklentisi: süre/özet gömülü; eşleşince ilk yöneticiyle oturum açar ve dosyayı siler.
    private static string BuildLoginPlugin(string hash, long expiresUnix)
    {
        const string template = @"<?php
/*
 * Plugin Name: AdaPanel Login
 * Description: AdaPanel tek-tik yonetici girisi (otomatik uretildi; tek kullanimlik, kendini siler).
 */
if (!defined('ABSPATH')) { exit; }
add_action('init', function () {
    if (empty($_GET['adapanel_sso'])) { return; }
    $self = __FILE__;
    $burn = static function () use ($self) { if (is_file($self)) { @unlink($self); } };
    if (time() > __EXPIRES__) { $burn(); wp_die('AdaPanel: giris baglantisinin suresi doldu.'); }
    $token = (string) $_GET['adapanel_sso'];
    if (!hash_equals('__HASH__', hash('sha256', $token))) { return; }
    $burn();
    $admins = get_users(array('role' => 'administrator', 'orderby' => 'ID', 'order' => 'ASC', 'number' => 1, 'fields' => array('ID')));
    if (empty($admins)) { wp_die('AdaPanel: yonetici hesabi bulunamadi.'); }
    $uid = (int) $admins[0]->ID;
    wp_clear_auth_cookie();
    wp_set_current_user($uid);
    wp_set_auth_cookie($uid, false, is_ssl());
    wp_safe_redirect(admin_url());
    exit;
}, 1);
";
        return template.Replace("__EXPIRES__", expiresUnix.ToString()).Replace("__HASH__", hash);
    }

    // panel "Klonla": kaynak WP'yi yeni bir staging sitesine kopyalar. Tüm parçalar mevcut servislerden bestelenir:
    // CreateSiteAsync(php) + robocopy + TransferDatabaseAsync(ayrı DB+kullanıcı) + wp-config DB yenileme + wp search-replace.
    public async Task<WordPressCloneResultDto> CloneAsync(string sourceSiteName, CloneWordPressRequest request, CancellationToken cancellationToken = default)
    {
        var source = await iisSiteService.GetSiteAsync(sourceSiteName, cancellationToken);
        var sourceRoot = string.IsNullOrWhiteSpace(source.ApplicationRootPath) ? source.PhysicalPath : source.ApplicationRootPath;
        var sourceConfigPath = Path.Combine(sourceRoot, "wp-config.php");
        if (string.IsNullOrWhiteSpace(sourceRoot) || !File.Exists(sourceConfigPath))
        {
            throw new AppException("wpcli.not_wordpress", 409, 409);
        }

        var targetName = (request.TargetSiteName ?? string.Empty).Trim();
        var targetHost = (request.TargetHostName ?? string.Empty).Trim().ToLowerInvariant();
        if (targetName.Length is < 2 or > 128 || !IsSafeName(targetName))
        {
            throw new AppException("wp.clone_invalid_target", 400, 400);
        }
        if (targetHost.Length is 0 or > 255 || !IsSafeHost(targetHost))
        {
            throw new AppException("wp.clone_invalid_host", 400, 400);
        }

        var sites = await iisSiteService.GetSitesAsync(cancellationToken);
        if (sites.Any(item => string.Equals(item.Name, targetName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException("wp.clone_target_exists", 409, 409);
        }

        // Kaynak wp-config DB bilgileri + kaynak/hedef URL.
        var sourceConfig = await File.ReadAllTextAsync(sourceConfigPath, cancellationToken);
        var srcDbName = ReadDefine(sourceConfig, "DB_NAME");
        if (srcDbName.Length == 0)
        {
            throw new AppException("wp.clone_config_unreadable", 409, 409);
        }
        var (srcDbHost, srcDbPort) = SplitHostPort(ReadDefine(sourceConfig, "DB_HOST"));
        var sourceUrl = BuildHttpBase(source.Bindings);
        var targetUrl = $"http://{targetHost}";

        // Hedef fiziksel yol: kaynağın kardeşi (aynı web kökü altında).
        var parent = Path.GetDirectoryName(sourceRoot.TrimEnd('\\', '/')) ?? sourceRoot;
        var targetRoot = Path.Combine(parent, SafeFolder(targetName));

        // 1) Hedef siteyi oluştur (PHP runtime). DB'yi ayrıca kopyalayacağız.
        Directory.CreateDirectory(targetRoot);
        await iisSiteService.CreateSiteAsync(new CreateSiteRequest
        {
            Name = targetName,
            PhysicalPath = targetRoot,
            HostName = targetHost,
            Protocol = "http",
            Port = 80,
            RuntimeProfile = "php",
            ProvisionDefaultServices = false,
            CreateDatabase = false,
            AutoStart = true
        }, cancellationToken);

        // 2) Dosyaları kopyala (robocopy /E; çıkış kodu < 8 başarı sayılır).
        var copy = await ProcessRunner.RunAsync(
            "robocopy",
            $"\"{sourceRoot.TrimEnd('\\')}\" \"{targetRoot.TrimEnd('\\')}\" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP",
            parent,
            cancellationToken,
            timeout: TimeSpan.FromMinutes(30));
        if (!RobocopyBasarili(copy.ExitCode))
        {
            throw new AppException("wp.clone_copy_failed", 500, 500, copy.StandardError);
        }

        // 3) Veritabanını AYRI bir hedef DB'ye kopyala (+ yeni kullanıcı, tam yetki, parola üret).
        var engine = await ResolveDbEngineAsync(cancellationToken);
        var targetDb = ResourceNaming.DatabaseNameFromSite(targetName);
        if (string.IsNullOrWhiteSpace(targetDb))
        {
            targetDb = "wp_clone";
        }
        if (string.Equals(targetDb, srcDbName, StringComparison.OrdinalIgnoreCase))
        {
            targetDb += "_stg";
        }
        var transfer = await databaseService.TransferDatabaseAsync(new DatabaseTransferRequest
        {
            Engine = engine,
            SourceHost = srcDbHost,
            SourcePort = srcDbPort,
            SourceUserName = ReadDefine(sourceConfig, "DB_USER"),
            SourcePassword = ReadDefine(sourceConfig, "DB_PASSWORD"),
            SourceDatabase = srcDbName,
            TargetDatabase = targetDb,
            TargetUserName = (targetDb.Length > 26 ? targetDb[..26] : targetDb) + "_u",
            TargetPassword = string.Empty
        }, cancellationToken);

        // 4) Hedef wp-config DB bilgilerini yenile (kopyalanan dosya hâlâ kaynağın DB'sine işaret ediyor).
        var targetConfigPath = Path.Combine(targetRoot, "wp-config.php");
        if (File.Exists(targetConfigPath))
        {
            var targetConfig = await File.ReadAllTextAsync(targetConfigPath, cancellationToken);
            targetConfig = ReplaceDefine(targetConfig, "DB_NAME", transfer.TargetDatabase);
            targetConfig = ReplaceDefine(targetConfig, "DB_USER", transfer.UserName);
            targetConfig = ReplaceDefine(targetConfig, "DB_PASSWORD", transfer.Password);
            targetConfig = ReplaceDefine(targetConfig, "DB_HOST", "127.0.0.1");
            await File.WriteAllTextAsync(targetConfigPath, targetConfig, cancellationToken);
        }

        // 5) URL arama-değiştir (hedef kökte wp-cli) → staging kendi adresinde çalışır.
        var urlDegistirildi = await TryRewriteSiteUrlAsync(targetName, sourceUrl, targetUrl, cancellationToken);

        return new WordPressCloneResultDto
        {
            TargetSiteName = targetName,
            TargetUrl = targetUrl,
            TargetDatabase = transfer.TargetDatabase,
            DatabaseUser = transfer.UserName,
            DatabasePassword = transfer.Password,
            UrlRewritten = urlDegistirildi
        };
    }

    // panel "Veri Kopyala": kaynak WP'nin dosya/DB'sini VAR OLAN hedefe taşır (staging→canlı). ÖNCE hedefi yedekler,
    // hedefin DB bağlantısını korur, URL arama-değiştir yapar. Motor admin'iyle dump/import (per-DB kullanıcı gerekmez).
    public async Task<WordPressCopyDataResultDto> CopyDataAsync(string sourceSiteName, CopyWordPressDataRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Files && !request.Database)
        {
            throw new AppException("wp.copy_nothing", 400, 400);
        }

        var source = await iisSiteService.GetSiteAsync(sourceSiteName, cancellationToken);
        var sourceRoot = string.IsNullOrWhiteSpace(source.ApplicationRootPath) ? source.PhysicalPath : source.ApplicationRootPath;
        if (string.IsNullOrWhiteSpace(sourceRoot) || !File.Exists(Path.Combine(sourceRoot, "wp-config.php")))
        {
            throw new AppException("wpcli.not_wordpress", 409, 409);
        }

        var targetName = (request.TargetSiteName ?? string.Empty).Trim();
        if (targetName.Length == 0 || string.Equals(targetName, sourceSiteName, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("wp.copy_invalid_target", 400, 400);
        }

        var target = await iisSiteService.GetSiteAsync(targetName, cancellationToken); // yoksa fırlatır
        var targetRoot = string.IsNullOrWhiteSpace(target.ApplicationRootPath) ? target.PhysicalPath : target.ApplicationRootPath;
        var targetConfigPath = Path.Combine(targetRoot, "wp-config.php");
        if (string.IsNullOrWhiteSpace(targetRoot) || !File.Exists(targetConfigPath))
        {
            throw new AppException("wp.copy_target_not_wp", 409, 409);
        }

        var sourceUrl = BuildHttpBase(source.Bindings);
        var targetUrl = BuildHttpBase(target.Bindings);
        var engine = await ResolveDbEngineAsync(cancellationToken);

        // Kopyalamadan ÖNCE hedefin yedeği (dosya + DB) → hedef kökünün kardeşi "_kopyala_yedek_<tarih>".
        var targetParent = Path.GetDirectoryName(targetRoot.TrimEnd('\\', '/')) ?? targetRoot;
        var backupRoot = Path.Combine(targetParent, Path.GetFileName(targetRoot.TrimEnd('\\', '/')) + "_kopyala_yedek_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backupRoot);

        // Hedefin korunacak DB bilgileri (dosya kopyalansa bile hedef kendi DB'sinde kalır).
        var targetConfig = await File.ReadAllTextAsync(targetConfigPath, cancellationToken);
        var targetDbName = ReadDefine(targetConfig, "DB_NAME");
        var targetDbUser = ReadDefine(targetConfig, "DB_USER");
        var targetDbPass = ReadDefine(targetConfig, "DB_PASSWORD");
        var targetDbHost = ReadDefine(targetConfig, "DB_HOST");

        // 1) Veritabanı: hedef DB'yi yedekle → kaynak DB'yi hedefin DB'sine aktar.
        if (request.Database)
        {
            var sourceConfig = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "wp-config.php"), cancellationToken);
            var sourceDbName = ReadDefine(sourceConfig, "DB_NAME");
            if (sourceDbName.Length == 0 || targetDbName.Length == 0)
            {
                throw new AppException("wp.copy_config_unreadable", 409, 409);
            }

            await databaseService.ExportDumpAsync(new DatabaseDumpRequest { Engine = engine, DatabaseName = targetDbName, FilePath = Path.Combine(backupRoot, "target-db.sql") }, cancellationToken);
            var sourceDump = Path.Combine(backupRoot, "source-db.sql");
            await databaseService.ExportDumpAsync(new DatabaseDumpRequest { Engine = engine, DatabaseName = sourceDbName, FilePath = sourceDump }, cancellationToken);
            await databaseService.ImportDumpAsync(new DatabaseDumpRequest { Engine = engine, DatabaseName = targetDbName, FilePath = sourceDump }, cancellationToken);
        }

        // 2) Dosyalar: hedefi yedekle (kopya) → kaynak→hedef aynala (/MIR) → hedef wp-config DB bilgilerini geri yaz.
        if (request.Files)
        {
            // YEDEĞİN SONUCU DENETLENİR — çünkü bir sonraki adım GERİ DÖNÜLEMEZ.
            //
            // Buradaki `/MIR` hedefte kaynakta olmayan HER ŞEYİ siler; yani bu yedek, işlemin tek
            // geri dönüş noktası. Sonucu okunmadığı için yedek patladığında (disk dolu, kilitli
            // dosya, erişim reddi, zaman aşımı) aynalama yine de çalışıyor ve müşterinin canlı
            // sitesi geri dönülemez biçimde eziliyordu — panel de "kopyalandı" diyordu.
            //
            // Yedek alınamıyorsa YIKICI ADIM HİÇ ÇALIŞMAZ: hedef olduğu gibi kalır, kullanıcı
            // gerçek sebebi görür. "Yedeksiz devam et" seçeneği bilerek yok.
            var backup = await ProcessRunner.RunAsync(
                "robocopy",
                $"\"{targetRoot.TrimEnd('\\')}\" \"{Path.Combine(backupRoot, "target-files").TrimEnd('\\')}\" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP",
                targetParent,
                cancellationToken,
                timeout: TimeSpan.FromMinutes(30));
            if (!RobocopyBasarili(backup.ExitCode))
            {
                throw new AppException("wp.copy_backup_failed", 500, 500, backup.StandardError);
            }

            var mirror = await ProcessRunner.RunAsync(
                "robocopy",
                $"\"{sourceRoot.TrimEnd('\\')}\" \"{targetRoot.TrimEnd('\\')}\" /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP",
                targetParent,
                cancellationToken,
                timeout: TimeSpan.FromMinutes(30));
            if (!RobocopyBasarili(mirror.ExitCode))
            {
                throw new AppException("wp.clone_copy_failed", 500, 500, mirror.StandardError);
            }

            // Aynalama kaynağın wp-config'ini getirdi → hedefin kendi DB bilgilerini geri yaz.
            var rewritten = await File.ReadAllTextAsync(targetConfigPath, cancellationToken);
            rewritten = ReplaceDefine(rewritten, "DB_NAME", targetDbName);
            rewritten = ReplaceDefine(rewritten, "DB_USER", targetDbUser);
            rewritten = ReplaceDefine(rewritten, "DB_PASSWORD", targetDbPass);
            rewritten = ReplaceDefine(rewritten, "DB_HOST", targetDbHost.Length > 0 ? targetDbHost : "127.0.0.1");
            await File.WriteAllTextAsync(targetConfigPath, rewritten, cancellationToken);
        }

        // 3) URL arama-değiştir (hedef DB'de) → hedef kendi adresinde çalışır.
        var urlDegistirildi = await TryRewriteSiteUrlAsync(targetName, sourceUrl, targetUrl, cancellationToken);

        return new WordPressCopyDataResultDto
        {
            TargetSiteName = targetName,
            TargetUrl = targetUrl,
            BackupPath = backupRoot,
            CopiedFiles = request.Files,
            CopiedDatabase = request.Database,
            UrlRewritten = urlDegistirildi
        };
    }

    private async Task<string> ResolveDbEngineAsync(CancellationToken cancellationToken)
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

    /// <summary>
    /// robocopy çıkış kodu başarı mı? robocopy 0-7 arasını BAŞARI sayar (0=değişiklik yok,
    /// 1=kopyalandı, 2=ek dosyalar, 4=uyumsuzluk...); 8 ve üstü gerçek hatadır.
    /// </summary>
    /// <remarks>
    /// NEGATİF KODLAR DA HATADIR ve eski <c>&gt;= 8</c> denetimi bunları KAÇIRIYORDU:
    /// <see cref="ProcessRunner"/> zaman aşımında süreci öldürüp <c>ExitCode = -1</c> döner.
    /// 30 dakikada bitmeyen bir kopyalama "başarılı" sayılıyordu — tam da yedeğin/aynalamanın
    /// yarım kaldığı durum. Tek kaynak: üç çağrı yeri de bu yüklemi kullanır.
    /// </remarks>
    internal static bool RobocopyBasarili(int exitCode) => exitCode is >= 0 and < 8;

    /// <summary>
    /// Kopyalanan WordPress'in veritabanındaki KAYNAK adresini hedef adresle değiştirir.
    /// Değiştirme gerçekten yapıldıysa (ya da hiç gerekmiyorsa) <c>true</c> döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// BAŞARISIZLIK ARTIK GİZLENMİYOR. Eski hâlinde çıkış kodu hiç okunmuyordu ve <c>catch</c>
    /// boştu; wp-cli yoksa, PHP çalışmıyorsa ya da DB'ye bağlanamıyorsa panel yine "Klon
    /// tamamlandı" diyordu. Sonuç sessiz değil AĞIR: WordPress site adresini veritabanından okur,
    /// yani adresi değişmemiş klon <b>ÜRETİM ADRESİNİ servis eder</b> — staging'de yapılan her
    /// tıklama canlı siteye gider, canlı veri değişir, arama motoru kopya içerik görür.
    /// </para>
    /// <para>
    /// Yine de İSTİSNA FIRLATILMIYOR: bu noktada hedef site ve veritabanı zaten oluşturulmuş
    /// durumda. Fırlatmak kullanıcıyı "ne oluştu bilmiyorum" hâlinde bırakırdı; doğru davranış
    /// panelin aynı dosyadaki <c>wp.updates_partial</c> deseni — kısmi başarıyı SÖYLEMEK.
    /// Çağıran bu bayrağı DTO'ya koyar, denetleyici de farklı bir mesaj anahtarı döndürür.
    /// </para>
    /// <para>
    /// Hata METNİ dışarı verilmiyor: wp-cli çıktısı wp-config'ten okunan veritabanı kullanıcı
    /// adı/parolasını içerebilir. Kullanıcının ihtiyacı olan bilgi "adres değişmedi, komutu
    /// yeniden çalıştır" — kimlik bilgisi değil.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <c>internal</c> (private değil): davranışı DIZGE ARAYARAK değil GERÇEKTEN KOŞTURARAK test
    /// edilebilsin diye. İlk yazımda bu koruma kaynak metninde <c>"sonuc.ExitCode == 0"</c> aranarak
    /// test edilmişti; mutasyonla denendi ve test YEŞİL KALDI — çünkü aynı dizge dosyanın başka
    /// yerlerinde de geçiyor. Dizge testi korunan şeyi değil bugünkü yazımını doğrular.
    /// </remarks>
    internal async Task<bool> TryRewriteSiteUrlAsync(
        string targetSiteName,
        string sourceUrl,
        string targetUrl,
        CancellationToken cancellationToken)
    {
        // Kaynak adresi okunamadıysa ya da adres zaten aynıysa değiştirilecek bir şey yok.
        if (sourceUrl.Length == 0 || targetUrl.Length == 0 || string.Equals(sourceUrl, targetUrl, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var sonuc = await hostingSettings.RunWpCliCommandAsync(
                targetSiteName,
                $"search-replace {sourceUrl} {targetUrl} --all-tables --skip-columns=guid",
                cancellationToken);
            return sonuc.ExitCode == 0;
        }
        catch (Exception)
        {
            // wp-cli/PHP hiç çalıştırılamadı (AppException: wpcli.not_wordpress, çalıştırıcı
            // hatası...). Yutulmuyor — çağırana "olmadı" diye bildiriliyor.
            return false;
        }
    }

    private static bool IsSafeName(string value) =>
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ' ');

    private static bool IsSafeHost(string value) =>
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '.');

    private static string SafeFolder(string value)
    {
        var cleaned = new string(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray()).Trim('_');
        return cleaned.Length == 0 ? "wp_clone" : cleaned;
    }

    private static (string Host, int Port) SplitHostPort(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return ("127.0.0.1", 3306);
        }
        var colon = value.IndexOf(':');
        if (colon <= 0)
        {
            return (value, 3306);
        }
        var host = value[..colon];
        return int.TryParse(value[(colon + 1)..], out var port) && port is > 0 and <= 65535 ? (host, port) : (host, 3306);
    }

    private static string BuildHttpBase(IEnumerable<SiteBindingDto> bindings)
    {
        var binding = bindings.FirstOrDefault(item => string.Equals(item.Protocol, "https", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.HostName))
                   ?? bindings.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.HostName))
                   ?? bindings.FirstOrDefault();
        if (binding is null || string.IsNullOrWhiteSpace(binding.HostName))
        {
            return string.Empty;
        }
        var isHttps = string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase);
        var scheme = isHttps ? "https" : "http";
        var portPart = binding.Port == 0 || (isHttps && binding.Port == 443) || (!isHttps && binding.Port == 80) ? string.Empty : $":{binding.Port}";
        return $"{scheme}://{binding.HostName}{portPart}";
    }

    // wp-config'ten define('KEY','deger') değerini okur (tek/çift tırnak; basit kaçış çözümü).
    private static string ReadDefine(string content, string key)
    {
        var match = Regex.Match(content, @"define\(\s*['""]" + Regex.Escape(key) + @"['""]\s*,\s*['""]([^'""]*)['""]");
        return match.Success
            ? match.Groups[1].Value.Replace("\\'", "'").Replace("\\\"", "\"").Replace("\\\\", "\\")
            : string.Empty;
    }

    // wp-config'teki define('KEY', '...') değerini yenisiyle değiştirir (yoksa içerik değişmez).
    private static string ReplaceDefine(string content, string key, string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("$", "$$");
        var pattern = @"(define\(\s*['""]" + Regex.Escape(key) + @"['""]\s*,\s*)['""][^'""]*['""](\s*\))";
        return Regex.Replace(content, pattern, "${1}'" + escaped + "'${2}");
    }

    // panel "Güvenlik": Plesk WP Toolkit önlem demetini uygular (mu-plugin runtime önlemleri + wp-cli config).
    public async Task<WordPressHardenResultDto> HardenAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "wp-config.php")))
        {
            throw new AppException("wpcli.not_wordpress", 409, 409);
        }

        var applied = new List<string>();

        // 1) mu-plugin: sürüm gizleme + XML-RPC pingback kapatma + yazar (?author=N) taraması engelleme.
        var muDir = Path.Combine(root, "wp-content", "mu-plugins");
        Directory.CreateDirectory(muDir);
        await File.WriteAllTextAsync(Path.Combine(muDir, "adapanel-security.php"), SecurityPluginContent(), cancellationToken);
        applied.Add("hide_version");
        applied.Add("xmlrpc_pingback");
        applied.Add("author_enum");

        // 2) wp-cli: dosya düzenleyiciyi kapat + güvenlik anahtarlarını yenile (best-effort; wp-cli yoksa atlanır).
        try
        {
            if ((await hostingSettings.RunWpCliCommandAsync(siteName, "config set DISALLOW_FILE_EDIT true --raw", cancellationToken)).ExitCode == 0)
            {
                applied.Add("file_edit");
            }
            if ((await hostingSettings.RunWpCliCommandAsync(siteName, "config shuffle-salts", cancellationToken)).ExitCode == 0)
            {
                applied.Add("salts");
            }
        }
        catch
        {
            // wp-cli yoksa runtime (mu-plugin) önlemleri yine de uygulanmıştır
        }

        return new WordPressHardenResultDto { Applied = applied };
    }

    private static string SecurityPluginContent() =>
@"<?php
/*
 * Plugin Name: AdaPanel Security
 * Description: AdaPanel guvenlik sikilastirmasi (surum gizleme, XML-RPC pingback kapatma, yazar taramasi engelleme).
 */
if (!defined('ABSPATH')) { exit; }
remove_action('wp_head', 'wp_generator');
add_filter('the_generator', '__return_empty_string');
add_filter('xmlrpc_methods', function ($methods) {
    unset($methods['pingback.ping'], $methods['pingback.extensions.getPingbacks']);
    return $methods;
});
add_action('template_redirect', function () {
    if (!is_admin() && isset($_GET['author']) && is_numeric($_GET['author'])) {
        wp_safe_redirect(home_url('/'), 301);
        exit;
    }
});
";

    // DetectAsync için (tek site): etkin eklenti/tema kümesini wp-cli ile öğrenip Active alanını doldurur (best-effort).
    private async Task EnrichActiveStateAsync(string siteName, WordPressSiteDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var activePlugins = await GetActiveSlugsAsync(siteName, "plugin list --status=active --field=name", cancellationToken);
            foreach (var plugin in dto.Plugins)
            {
                plugin.Active = activePlugins.Contains(plugin.Slug);
            }
            var activeThemes = await GetActiveSlugsAsync(siteName, "theme list --status=active --field=name", cancellationToken);
            foreach (var theme in dto.Themes)
            {
                theme.Active = activeThemes.Contains(theme.Slug);
            }
        }
        catch
        {
            // wp-cli yoksa/çalışmazsa etkin durum gösterilmez (sorun değil)
        }
    }

    private async Task<HashSet<string>> GetActiveSlugsAsync(string siteName, string command, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = await hostingSettings.RunWpCliCommandAsync(siteName, command, cancellationToken);
        if (result.ExitCode == 0)
        {
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                set.Add(line);
            }
        }
        return set;
    }

    private static WordPressSiteDto BuildDto(string siteName, string root, string? hostName, string latestVersion = "")
    {
        var dto = new WordPressSiteDto { SiteName = siteName, Path = root };
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "wp-config.php")))
        {
            return dto;
        }

        dto.IsWordPress = true;
        dto.MaintenanceMode = File.Exists(Path.Combine(root, ".maintenance"));
        dto.Version = ReadVersion(root);
        dto.Plugins = ReadPlugins(root);
        dto.Themes = ReadThemes(root);
        dto.LatestVersion = latestVersion;
        dto.UpdateAvailable = IsOlderVersion(dto.Version, latestVersion);
        dto.Security = BuildSecurity(root);
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            dto.AdminUrl = $"http://{hostName}/wp-admin/";
        }

        return dto;
    }

    // Güvenlik durumunu dosya tabanlı okur: mu-plugin varlığı (sürüm/xmlrpc/yazar) + wp-config DISALLOW_FILE_EDIT.
    private static WordPressSecurityDto BuildSecurity(string root)
    {
        var pluginActive = File.Exists(Path.Combine(root, "wp-content", "mu-plugins", "adapanel-security.php"));
        var fileEditorDisabled = false;
        var configPath = Path.Combine(root, "wp-config.php");
        if (File.Exists(configPath))
        {
            try
            {
                fileEditorDisabled = Regex.IsMatch(
                    File.ReadAllText(configPath),
                    @"define\(\s*['""]DISALLOW_FILE_EDIT['""]\s*,\s*true\b",
                    RegexOptions.IgnoreCase);
            }
            catch
            {
                // wp-config okunamazsa kapalı say
            }
        }

        return new WordPressSecurityDto
        {
            FileEditorDisabled = fileEditorDisabled,
            VersionHidden = pluginActive,
            XmlRpcPingbackDisabled = pluginActive,
            AuthorScanBlocked = pluginActive
        };
    }

    // wp-content/plugins: her klasörde "Plugin Name:" başlıklı ana php dosyası + tek-dosyalık eklentiler.
    private static IReadOnlyCollection<WordPressComponentDto> ReadPlugins(string root)
    {
        var pluginsDir = Path.Combine(root, "wp-content", "plugins");
        var list = new List<WordPressComponentDto>();
        if (!Directory.Exists(pluginsDir))
        {
            return list;
        }

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(pluginsDir))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.php", SearchOption.TopDirectoryOnly))
                {
                    var header = ReadHeader(file);
                    if (header.TryGetValue("Plugin Name", out var name))
                    {
                        list.Add(new WordPressComponentDto
                        {
                            Slug = Path.GetFileName(folder),
                            Name = name,
                            Version = header.GetValueOrDefault("Version", string.Empty)
                        });
                        break;
                    }
                }
            }

            foreach (var file in Directory.EnumerateFiles(pluginsDir, "*.php", SearchOption.TopDirectoryOnly))
            {
                var header = ReadHeader(file);
                if (header.TryGetValue("Plugin Name", out var name))
                {
                    list.Add(new WordPressComponentDto
                    {
                        Slug = Path.GetFileNameWithoutExtension(file),
                        Name = name,
                        Version = header.GetValueOrDefault("Version", string.Empty)
                    });
                }
            }
        }
        catch
        {
            // erişilemeyen klasör/dosya atlanır
        }

        return list.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // wp-content/themes/*/style.css başlığından temalar.
    private static IReadOnlyCollection<WordPressComponentDto> ReadThemes(string root)
    {
        var themesDir = Path.Combine(root, "wp-content", "themes");
        var list = new List<WordPressComponentDto>();
        if (!Directory.Exists(themesDir))
        {
            return list;
        }

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(themesDir))
            {
                var style = Path.Combine(folder, "style.css");
                if (!File.Exists(style))
                {
                    continue;
                }

                var header = ReadHeader(style);
                list.Add(new WordPressComponentDto
                {
                    Slug = Path.GetFileName(folder),
                    Name = header.GetValueOrDefault("Theme Name", Path.GetFileName(folder)),
                    Version = header.GetValueOrDefault("Version", string.Empty)
                });
            }
        }
        catch
        {
            // erişilemeyen klasör/dosya atlanır
        }

        return list.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // WordPress eklenti/tema başlığı: dosyanın ilk ~8KB'ında "Anahtar: Değer" satırları.
    private static Dictionary<string, string> ReadHeader(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var reader = new StreamReader(file);
            var buffer = new char[8192];
            var read = reader.Read(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, read);
            foreach (Match match in HeaderRegex().Matches(text))
            {
                var key = match.Groups[1].Value.Trim();
                if (!result.ContainsKey(key))
                {
                    result[key] = match.Groups[2].Value.Trim();
                }
            }
        }
        catch
        {
            // okunamayan dosya boş başlık döner
        }

        return result;
    }

    private static string ReadVersion(string root)
    {
        var versionFile = Path.Combine(root, "wp-includes", "version.php");
        if (!File.Exists(versionFile))
        {
            return string.Empty;
        }

        try
        {
            var content = File.ReadAllText(versionFile);
            var match = VersionRegex().Match(content);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // En güncel kararlı WordPress çekirdek sürümü (wordpress.org), süreç ömrü boyunca 6 saat cache'li.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static string cachedLatestVersion = string.Empty;
    private static DateTime cachedLatestAtUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim VersionLock = new(1, 1);

    private static async Task<string> GetLatestCoreVersionAsync(CancellationToken cancellationToken)
    {
        if (cachedLatestVersion.Length > 0 && DateTime.UtcNow - cachedLatestAtUtc < TimeSpan.FromHours(6))
        {
            return cachedLatestVersion;
        }

        await VersionLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedLatestVersion.Length > 0 && DateTime.UtcNow - cachedLatestAtUtc < TimeSpan.FromHours(6))
            {
                return cachedLatestVersion;
            }

            using var response = await Http.GetAsync("https://api.wordpress.org/core/version-check/1.7/", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("offers", out var offers)
                && offers.ValueKind == JsonValueKind.Array
                && offers.GetArrayLength() > 0
                && offers[0].TryGetProperty("current", out var current)
                && current.ValueKind == JsonValueKind.String)
            {
                cachedLatestVersion = current.GetString() ?? string.Empty;
                cachedLatestAtUtc = DateTime.UtcNow;
            }
        }
        catch
        {
            // İnternet yok / API erişilemez: güncelleme kontrolü sessizce atlanır (mevcut cache korunur).
        }
        finally
        {
            VersionLock.Release();
        }

        return cachedLatestVersion;
    }

    // installed < latest mi (her ikisi de geçerli sürümse). Belirsizse false (güncelleme önerme).
    private static bool IsOlderVersion(string installed, string latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        return Version.TryParse(Normalize(installed), out var installedVersion)
            && Version.TryParse(Normalize(latest), out var latestVersion)
            && installedVersion < latestVersion;

        static string Normalize(string value)
        {
            var core = value.Split('-', '+')[0];
            var parts = core.Split('.');
            return parts.Length switch
            {
                1 => $"{parts[0]}.0",
                _ => core
            };
        }
    }

    [GeneratedRegex(@"\$wp_version\s*=\s*'([^']+)'")]
    private static partial Regex VersionRegex();

    // Eklenti/tema başlık satırı: olası yorum işaretlerinden sonra "Anahtar: Değer".
    [GeneratedRegex(@"(?m)^[ \t/*#@]*([A-Za-z][A-Za-z ]+):[ \t]*(.+?)[ \t]*$")]
    private static partial Regex HeaderRegex();

    // --- Guncelleme yonetimi ---

    /// <inheritdoc />
    public async Task<WordPressUpdatesDto> GetUpdatesAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var sonuc = new WordPressUpdatesDto { SiteName = siteName };
        var ogeler = new List<WordPressUpdatableDto>();
        var hatalar = new List<string>();

        // CEKIRDEK. Guncelleme yoksa wp-cli BOS dizi doner; bu hata degildir.
        var cekirdek = await WpCliJsonAsync(siteName, "core check-update --format=json --fields=version,update_type", cancellationToken);
        if (cekirdek.Error.Length > 0)
        {
            hatalar.Add(cekirdek.Error);
        }
        else if (cekirdek.Items is not null)
        {
            var mevcut = await WpCliRawAsync(siteName, "core version", cancellationToken);
            foreach (var oge in cekirdek.Items)
            {
                ogeler.Add(new WordPressUpdatableDto
                {
                    Kind = "core",
                    Name = "core",
                    Title = "WordPress",
                    CurrentVersion = mevcut,
                    AvailableVersion = Alan(oge, "version"),
                    UpdateAvailable = true
                });
            }
        }

        // EKLENTI ve TEMA.
        foreach (var (tur, komut) in new[]
                 {
                     ("plugin", "plugin list --format=json --fields=name,title,status,version,update,update_version"),
                     ("theme", "theme list --format=json --fields=name,title,status,version,update,update_version")
                 })
        {
            var liste = await WpCliJsonAsync(siteName, komut, cancellationToken);
            if (liste.Error.Length > 0)
            {
                hatalar.Add(liste.Error);
                continue;
            }

            foreach (var oge in liste.Items ?? Array.Empty<JsonElement>())
            {
                var guncelVar = string.Equals(Alan(oge, "update"), "available", StringComparison.OrdinalIgnoreCase);
                var baslik = Alan(oge, "title");
                ogeler.Add(new WordPressUpdatableDto
                {
                    Kind = tur,
                    Name = Alan(oge, "name"),
                    Title = baslik.Length > 0 ? baslik : Alan(oge, "name"),
                    CurrentVersion = Alan(oge, "version"),
                    AvailableVersion = guncelVar ? Alan(oge, "update_version") : string.Empty,
                    UpdateAvailable = guncelVar,
                    Status = Alan(oge, "status")
                });
            }
        }

        // OLCULEMEDIYSE "guncelleme yok" DEMIYORUZ. Bos liste, guvenlik acigi olan bir siteyi
        // guvende gosterebilirdi; okunamayan durum ayri bildiriliyor.
        sonuc.Measured = hatalar.Count == 0;
        sonuc.Error = string.Join(" | ", hatalar);
        sonuc.Items = ogeler
            .OrderByDescending(item => item.UpdateAvailable)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return sonuc;
    }

    /// <inheritdoc />
    public async Task<WordPressUpdateResultDto> ApplyUpdatesAsync(string siteName, WordPressUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var sonuclar = new List<WordPressUpdateItemResultDto>();

        // HER OGE AYRI calistirilir. Tek komutta toplamak, biri patlayinca kalanlarin da
        // yapilmamasina yol acardi; kullanici hangisinin gectigini de goremezdi.
        if (request.Core)
        {
            sonuclar.Add(await GuncelleAsync(siteName, "core", "core", "core update", cancellationToken));
        }

        foreach (var eklenti in request.Plugins.Select(Slug).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sonuclar.Add(await GuncelleAsync(siteName, "plugin", eklenti, "plugin update " + eklenti, cancellationToken));
        }

        foreach (var tema in request.Themes.Select(Slug).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sonuclar.Add(await GuncelleAsync(siteName, "theme", tema, "theme update " + tema, cancellationToken));
        }

        return new WordPressUpdateResultDto { SiteName = siteName, Results = sonuclar };
    }

    private async Task<WordPressUpdateItemResultDto> GuncelleAsync(string siteName, string tur, string ad, string komut, CancellationToken cancellationToken)
    {
        var sonuc = await hostingSettings.RunWpCliCommandAsync(siteName, komut, cancellationToken);
        return new WordPressUpdateItemResultDto
        {
            Kind = tur,
            Name = ad,
            Success = sonuc.ExitCode == 0,
            Message = sonuc.ExitCode == 0
                ? string.Empty
                : Kisalt(string.IsNullOrWhiteSpace(sonuc.Error) ? sonuc.Output : sonuc.Error)
        };
    }

    /// <summary>
    /// wp-cli'yi calistirip JSON dizisini ayristirir.
    /// </summary>
    /// <remarks>
    /// Cikis kodu 0 olsa bile cikti JSON olmayabilir (uyari satirlari). Ayristirilamayan cikti
    /// "bos liste" SAYILMAZ, hata sayilir -- aksi halde okunamayan bir site "guncel" gorunurdu.
    /// </remarks>
    private async Task<(IReadOnlyList<JsonElement>? Items, string Error)> WpCliJsonAsync(string siteName, string komut, CancellationToken cancellationToken)
    {
        var sonuc = await hostingSettings.RunWpCliCommandAsync(siteName, komut, cancellationToken);
        if (sonuc.ExitCode != 0)
        {
            return (null, Kisalt(string.IsNullOrWhiteSpace(sonuc.Error) ? sonuc.Output : sonuc.Error));
        }

        var metin = (sonuc.Output ?? string.Empty).Trim();
        var bas = metin.IndexOf('[');
        var son = metin.LastIndexOf(']');
        if (bas < 0 || son <= bas)
        {
            // wp-cli bos liste icin "[]" yazar; hic kose parantez yoksa cikti beklenmedik demektir.
            return (null, Kisalt(metin.Length == 0 ? "wp-cli bos cikti dondu" : metin));
        }

        try
        {
            using var belge = JsonDocument.Parse(metin.Substring(bas, son - bas + 1));
            return (belge.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray(), string.Empty);
        }
        catch (JsonException exception)
        {
            return (null, Kisalt(exception.Message));
        }
    }

    private async Task<string> WpCliRawAsync(string siteName, string komut, CancellationToken cancellationToken)
    {
        var sonuc = await hostingSettings.RunWpCliCommandAsync(siteName, komut, cancellationToken);
        return sonuc.ExitCode == 0 ? (sonuc.Output ?? string.Empty).Trim() : string.Empty;
    }

    private static string Alan(JsonElement oge, string ad)
    {
        if (oge.ValueKind != JsonValueKind.Object || !oge.TryGetProperty(ad, out var deger))
        {
            return string.Empty;
        }

        return deger.ValueKind == JsonValueKind.String ? deger.GetString() ?? string.Empty : deger.ToString();
    }

    /// <summary>
    /// Eklenti/tema adini GUVENLI hale getirir: ad wp-cli komut satirina gidiyor.
    /// </summary>
    /// <remarks>
    /// WordPress slug'lari yalniz harf, rakam, tire ve alt cizgi icerir. Suzgec, kullanicidan gelen
    /// adin komuta ek arguman ya da bayrak sokmasini engeller.
    /// </remarks>
    private static string Slug(string? value)
    {
        var kaynak = (value ?? string.Empty).Trim();
        return new string(kaynak.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
    }

    private static string Kisalt(string? value)
    {
        var metin = (value ?? string.Empty).Trim();
        return metin.Length <= 500 ? metin : metin.Substring(0, 500) + "...";
    }

}
