using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Roundcube webmail'i IIS PHP sitesi olarak kurar/yayınlar ve Stalwart'a (IMAP 143 +
/// Submission 587, STARTTLS) bağlar. phpMyAdmin/Adminer ile aynı dağıtım kalıbını izler.
/// SQLite kullanır (ayrı DB sunucusu gerekmez; Roundcube ilk bağlantıda şemayı kurar).
/// </summary>
public sealed class WebmailService(
    AdaPanelDbContext dbContext,
    IComponentBootstrapper componentBootstrapper,
    IIisSiteService iisSiteService,
    IPhpService phpService,
    IHttpClientFactory httpClientFactory,
    JsonFileStore fileStore,
    ISslService sslService,
    ILogger<WebmailService> logger) : IWebmailService
{
    private const string StatePath = "config/webmail.json";
    private const string ComponentId = "roundcube";
    private const string SiteName = "AdaPanel.Webmail";
    private const int DefaultPort = 8899;
    private const string ManagedMarker = "Managed by AdaPanel";

    // Roundcube 1.7 için gereken/önerilen PHP eklentileri. DLL yoksa atlanır (PhpService).
    // Çekirdek (dom/xml/json/session/ctype/filter/pcre) Windows PHP'ye gömülüdür, listelenmez.
    private static readonly string[] RoundcubeExtensions =
    {
        "pdo_sqlite", "sqlite3", "intl", "mbstring", "openssl",
        "fileinfo", "zip", "gd", "exif", "iconv", "curl", "gettext"
    };

    public async Task<WebmailOpenDto> OpenAsync(string publicHost, CancellationToken cancellationToken = default)
    {
        // HIZLI YOL: webmail zaten kurulu, site ayakta ve sağlıklıysa kurulumu TEKRARLAMA — anında
        // URL dön. (Eskiden her tıkta siteyi silip yeniden kuruyor, tüm ağaca izin veriyor, config
        // yeniden yazıyor, sağlık problarını bekliyordu → uzun bekleme.) Ağır kurulum yalnız ilk
        // sefer ya da site/sağlık bozuksa çalışır.
        var existing = await fileStore.ReadAsync<WebmailState?>(StatePath, null, cancellationToken);
        if (existing is { Port: > 0 } && await IsDeployedAndHealthyAsync(existing.Port, cancellationToken))
        {
            return BuildWebmailUrl(existing, publicHost);
        }

        var component = await EnsureInstalledAsync(cancellationToken);
        var phpComponentId = await EnsurePhpInstalledAsync(cancellationToken);

        // Roundcube'un ihtiyaç duyduğu PHP eklentilerini (mevcut PHP'de de) etkinleştir. Çoğu zaten
        // açık; gd/exif/iconv gibi tam özellik için gerekenler de garanti altına alınır. Site daha
        // SONRA oluşturulduğu için yeni FastCGI süreci güncel php.ini'yi okur.
        await phpService.EnsureExtensionsAsync(phpComponentId, RoundcubeExtensions, cancellationToken);

        // Roundcube 1.5+ yapısı: web BELGE KÖKÜ public_html/ alt klasörüdür; config/, program/,
        // vendor/, temp/, logs/ ise bunun ÜSTÜNDE (kök) durur (güvenlik için webroot dışında).
        // IIS'i public_html'e yönlendirmezsek kök index.php "point your server to /public_html"
        // uyarısını gösterir.
        var installRoot = ResolveInstallRoot(component.InstallDir);
        var documentRoot = Path.Combine(installRoot, "public_html");

        EnsureRoundcubeConfig(installRoot);
        EnsureAssetJunctions(installRoot, documentRoot);
        WebAppPublisher.EnsurePhpEntry(documentRoot);
        EnsureHealthEndpoint(documentRoot);

        var state = await fileStore.ReadAsync<WebmailState?>(StatePath, null, cancellationToken);
        var requestedPort = state?.Port is > 0 ? state.Port : DefaultPort;

        await DeleteManagedSiteIfPresentAsync(cancellationToken);
        var createdSite = await CreateSiteAsync(documentRoot, requestedPort, cancellationToken);

        // Webmail'i profesyonel paneller gibi HER ZAMAN sıcak tut (app pool AlwaysRunning + idle timeout kapalı) ki
        // "her tıkta uzun bekletiyor" / soğuk başlangıç sağlık-kontrol timeout'u olmasın.
        await KeepAppPoolWarmAsync($"AdaPanel.{SiteName}", cancellationToken);

        var port = createdSite.Bindings
            .Where(binding =>
                binding.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(binding.HostName))
            .Select(binding => binding.Port)
            .FirstOrDefault();
        if (port <= 0)
        {
            throw new AppException("mail.webmail_deploy_failed", 500, 500, $"IIS did not create a wildcard HTTP binding for '{SiteName}'.");
        }

        await phpService.RegisterFastCgiAsync(phpComponentId, cancellationToken);
        await phpService.SetSitePhpVersionAsync(SiteName, new SetSitePhpVersionRequest { ComponentId = phpComponentId }, cancellationToken);

        // Webmail portunu Windows Güvenlik Duvarı'nda aç (uzak tarayıcıdan erişilebilsin). Best-effort.
        OpenFirewallPort(port);

        await fileStore.WriteAsync(StatePath, new WebmailState
        {
            SiteName = SiteName,
            Port = port,
            EntryRoot = documentRoot,
            PhpComponentId = phpComponentId
        }, cancellationToken);

        await VerifyHealthyAsync(port, cancellationToken);

        return BuildUrl(publicHost, port);
    }

    // profesyonel paneller gibi: bir mail domaini SSL alınca webmail.<domain> de otomatik HTTPS olur. Best-effort.
    public async Task SecureWebmailForDomainAsync(string domain, string contactEmail, CancellationToken cancellationToken = default)
    {
        var normalized = (domain ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith("webmail.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var webmailHost = $"webmail.{normalized}";
        try
        {
            var state = await fileStore.ReadAsync<WebmailState?>(StatePath, null, cancellationToken);
            if (state is null || state.Port <= 0)
            {
                return; // webmail henüz kurulmamış → güvenli yapacak bir şey yok
            }

            // 1) win-acme HTTP-01 doğrulaması + servis için webmail sitesine host-header HTTP binding ekle (idempotent).
            await EnsureWebmailHostBindingAsync(webmailHost, cancellationToken);

            // 2) host'u HTTPS yap (mevcut kapsayan cert ya da Let's Encrypt). Başarısızsa sessiz geç.
            if (!await sslService.TrySecureHostAsync(SiteName, webmailHost, contactEmail ?? string.Empty, cancellationToken))
            {
                return;
            }

            // 3) başarılı → kaydet (OpenAsync artık https döner).
            var hosts = new List<string>(state.SecuredHosts ?? new List<string>());
            if (!hosts.Contains(webmailHost, StringComparer.OrdinalIgnoreCase))
            {
                hosts.Add(webmailHost);
                state.SecuredHosts = hosts;
                await fileStore.WriteAsync(StatePath, state, cancellationToken);
            }

            logger.LogInformation("Webmail HTTPS etkin: {Host}", webmailHost);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Webmail HTTPS yapılamadı: {Host} (best-effort).", webmailHost);
        }
    }

    private static async Task EnsureWebmailHostBindingAsync(string webmailHost, CancellationToken cancellationToken)
    {
        // Paylaşımlı webmail sitesine webmail.<domain>:80 host-header binding'i ekle (challenge + servis).
        // New-WebBinding zaten varsa hata verir, SilentlyContinue ile yutulur → idempotent. Mevcut :8899 erişimi etkilenmez.
        var script = $$"""
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            New-WebBinding -Name '{{SiteName}}' -Protocol http -Port 80 -HostHeader '{{webmailHost}}' -ErrorAction SilentlyContinue
            """;
        await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
    }

    private async Task<InstalledRoundcube> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        var installed = await dbContext.InstalledComponents
            .Where(item => item.ComponentId == ComponentId && item.Status == ComponentInstallStatus.Installed)
            .Select(item => new InstalledRoundcube(item.InstallDir))
            .FirstOrDefaultAsync(cancellationToken);
        if (installed is not null)
        {
            return installed;
        }

        var result = await componentBootstrapper.InstallAsync(ComponentId, null, null, cancellationToken);
        return new InstalledRoundcube(result.InstallDir);
    }

    private async Task<string> EnsurePhpInstalledAsync(CancellationToken cancellationToken)
    {
        var installedPhp = await dbContext.InstalledComponents
            .Where(item => item.Status == ComponentInstallStatus.Installed && item.ComponentId.StartsWith("php-"))
            .OrderByDescending(item => item.ComponentId)
            .Select(item => item.ComponentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(installedPhp))
        {
            return installedPhp;
        }

        var result = await componentBootstrapper.InstallAsync("php-8.3", null, null, cancellationToken);
        return result.ComponentId;
    }

    // Roundcube kurulum kökü: program/ + public_html/ birlikte bulunur. tar.gz tek üst klasöre
    // açılır ve FlattenSingleTopDirectory ile düzleştirilir; yine de derinlik fark etmeden ararız.
    private static string ResolveInstallRoot(string installDir)
    {
        if (Directory.Exists(Path.Combine(installDir, "program")) && Directory.Exists(Path.Combine(installDir, "public_html")))
        {
            return installDir;
        }

        var match = Directory.EnumerateDirectories(installDir, "public_html", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(directory => directory is not null && Directory.Exists(Path.Combine(directory!, "program")))
            .OrderBy(directory => directory!.Length)
            .FirstOrDefault();
        return match ?? installDir;
    }

    private void EnsureRoundcubeConfig(string installRoot)
    {
        var configDir = Path.Combine(installRoot, "config");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "config.inc.php");

        if (File.Exists(configPath) &&
            !File.ReadAllText(configPath).Contains(ManagedMarker, StringComparison.OrdinalIgnoreCase))
        {
            // Yönetici elle düzenlemişse koru.
            return;
        }

        // IIS app pool kimliği (IIS_IUSRS) tüm Roundcube ağacını OKUYUP çalıştırabilmeli
        // (program/, vendor/, config/, skins/ ...). %ProgramData% altındaki tool dizini varsayılan
        // olarak okunabilir olmayabilir; bu yüzden köke özyinelemeli okuma+çalıştırma izni veriyoruz.
        GrantAppPool(installRoot, "RX");

        // Çalışma zamanında YAZILABİLİR dizinler (Roundcube temp/log + SQLite veritabanı).
        var dataDir = Path.Combine(installRoot, "adapanel-data");
        var tempDir = Path.Combine(installRoot, "temp");
        var logDir = Path.Combine(installRoot, "logs");
        foreach (var directory in new[] { dataDir, tempDir, logDir })
        {
            Directory.CreateDirectory(directory);
        }
        GrantAppPool(dataDir, "M");
        GrantAppPool(tempDir, "M");
        GrantAppPool(logDir, "M");

        var dbPath = Path.Combine(dataDir, "roundcube.db").Replace('\\', '/');
        var desKey = GenerateDesKey();

        // STARTTLS ile localhost'a bağlanırız; sertifika CN'i mail.<domain> olduğundan loopback'te
        // doğrulamayı kapatırız (allow_self_signed) — trafik yine TLS ile şifrelenir.
        var config = $$"""
            <?php
            // {{ManagedMarker}}. Custom files without this marker are preserved.
            $config = [];
            $config['db_dsnw'] = 'sqlite:///{{dbPath}}?mode=0646';
            $config['imap_host'] = 'tls://localhost:143';
            $config['smtp_host'] = 'tls://localhost:587';
            $config['smtp_user'] = '%u';
            $config['smtp_pass'] = '%p';
            $config['imap_conn_options'] = ['ssl' => ['verify_peer' => false, 'verify_peer_name' => false, 'allow_self_signed' => true]];
            $config['smtp_conn_options'] = ['ssl' => ['verify_peer' => false, 'verify_peer_name' => false, 'allow_self_signed' => true]];
            $config['support_url'] = '';
            $config['product_name'] = 'AdaPanel Webmail';
            $config['des_key'] = '{{desKey}}';
            $config['enable_installer'] = false;
            // IIS'te URL rewrite / static.php PATH_INFO'ya bel bağlamamak için: skins/program/plugins
            // public_html içine junction'landı ve varlıklar './' önekiyle düz statik dosya olarak verilir.
            $config['assets_path'] = './';
            $config['plugins'] = [];
            $config['temp_dir'] = '{{tempDir.Replace('\\', '/')}}';
            $config['log_dir'] = '{{logDir.Replace('\\', '/')}}';
            $config['skin'] = 'elastic';
            $config['session_lifetime'] = 30;
            """;

        File.WriteAllText(configPath, config, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // IIS app pool kimliği (IIS_IUSRS üyesi) için bir dizine özyinelemeli izin verir.
    // permission: "RX" (okuma+çalıştırma) ya da "M" (değiştirme/yazma).
    private void GrantAppPool(string directory, string permission)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("icacls.exe", $"\"{directory}\" /grant \"IIS_IUSRS:(OI)(CI){permission}\" /T /C /Q")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(30000);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not grant IIS_IUSRS {Permission} on {Directory} for webmail.", permission, directory);
        }
    }

    // Hızlı yol kontrolü: IIS sitesi var + ayakta + sağlık ucu yanıt veriyor mu (tek deneme).
    private async Task<bool> IsDeployedAndHealthyAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(SiteName, cancellationToken);
            if (!site.State.Equals("Started", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
            return false; // site yok / okunamıyor → tam kurulum gerekir
        }

        var (ok, _) = await ProbeAsync(port, "/adapanel-health.php", "AdaPanel-Webmail-OK", 1, cancellationToken);
        return ok;
    }

    private static WebmailOpenDto BuildUrl(string publicHost, int port)
    {
        var host = string.IsNullOrWhiteSpace(publicHost) ? "127.0.0.1" : publicHost.Trim();
        return new WebmailOpenDto { Url = $"http://{host}:{port}/" };
    }

    // Bir domain için webmail HTTPS yapıldıysa (webmail.<domain>) uyarısız HTTPS adresini tercih et; yoksa düz HTTP.
    private static WebmailOpenDto BuildWebmailUrl(WebmailState state, string publicHost)
    {
        if (state.SecuredHosts is { Count: > 0 })
        {
            return new WebmailOpenDto { Url = $"https://{state.SecuredHosts[0]}/" };
        }

        return BuildUrl(publicHost, state.Port);
    }

    private async Task VerifyHealthyAsync(int port, CancellationToken cancellationToken)
    {
        // 1) PHP/IIS hattı çalışıyor mu (düz PHP)?
        var (phpOk, phpDetails) = await ProbeAsync(port, "/adapanel-health.php", "AdaPanel-Webmail-OK", 8, cancellationToken);
        if (!phpOk)
        {
            throw new AppException("mail.webmail_deploy_failed", 500, 500,
                $"PHP/IIS hattı yanıt vermedi (port {port}). PHP kurulu/FastCGI kayıtlı mı?\r\n{phpDetails}");
        }

        // 2) Roundcube uygulaması gerçekten açılıyor mu (eksik PHP eklentisi/yapılandırma burada yakalanır)?
        var (appOk, appDetails) = await ProbeAsync(port, "/", null, 4, cancellationToken);
        if (!appOk)
        {
            throw new AppException("mail.webmail_deploy_failed", 500, 500,
                $"Roundcube açılışı başarısız (port {port}). Genelde eksik PHP eklentisi (pdo_sqlite/intl/openssl/mbstring) ya da yazma izni.\r\n{appDetails}");
        }
    }

    private async Task<(bool Ok, string Details)> ProbeAsync(int port, string path, string? marker, int attempts, CancellationToken cancellationToken)
    {
        var details = "(yanıt yok)";
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // PHP soğuk başlangıcı (ilk uyanış) birkaç saniye sürebilir → 12sn tolerans (5sn kısaydı).
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
                using var response = await httpClientFactory.CreateClient("AdaPanel.LocalHealth").SendAsync(request, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                var statusOk = (int)response.StatusCode is >= 200 and < 400;
                if (statusOk && (marker is null || body.Contains(marker, StringComparison.Ordinal)))
                {
                    return (true, string.Empty);
                }

                var preview = body.Length <= 600 ? body : body[..600] + "…";
                details = $"Url=http://127.0.0.1:{port}{path}\r\nStatus={(int)response.StatusCode}\r\nBody={preview}";
            }
            catch (Exception exception)
            {
                details = $"Url=http://127.0.0.1:{port}{path}\r\n{exception.GetType().Name}: {exception.Message}";
            }

            if (attempt + 1 < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
            }
        }

        return (false, details);
    }

    // Roundcube'un statik varlık ağaçları (skins/program/plugins) webroot'un (public_html) ÜSTÜNDE
    // durur. assets_path='./' ile bunlara './skins/...' gibi URL verilir; IIS bunları düz statik dosya
    // olarak servis edebilsin diye public_html içine ilgili klasörlere directory junction açıyoruz.
    // Junction (/J) ayrıcalık gerektirmez; hassas dizinler (config/temp/logs/vendor) LİNKLENMEZ.
    private void EnsureAssetJunctions(string installRoot, string documentRoot)
    {
        foreach (var name in new[] { "skins", "program", "plugins" })
        {
            var target = Path.Combine(installRoot, name);
            var link = Path.Combine(documentRoot, name);
            if (!Directory.Exists(target) || Directory.Exists(link) || File.Exists(link))
            {
                // Hedef yoksa atla; link zaten varsa (junction ya da klasör) dokunma.
                continue;
            }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit(15000);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not create webmail asset junction {Link} -> {Target}.", link, target);
            }
        }
    }

    // App pool'u sürekli ayakta tutar (soğuk başlangıç → yavaşlık/timeout olmasın). Best-effort.
    private static async Task KeepAppPoolWarmAsync(string appPoolName, CancellationToken cancellationToken)
    {
        var pool = appPoolName.Replace("'", "''");
        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $pool = '{{pool}}'
            if (Test-Path "IIS:\AppPools\$pool") {
              Set-ItemProperty "IIS:\AppPools\$pool" -Name startMode -Value 'AlwaysRunning'
              Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
              Start-WebAppPool -Name $pool -ErrorAction SilentlyContinue
            }
            """;
        await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
    }

    // Webmail HTTP portunu Windows Güvenlik Duvarı'nda açar (uzak tarayıcı erişimi). Best-effort.
    private void OpenFirewallPort(int port)
    {
        try
        {
            var ruleName = $"AdaPanel Webmail {port}";
            // Aynı isimli eski kuralı temizleyip yeniden ekle (idempotent).
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not open firewall for webmail port {Port}.", port);
        }
    }

    private static void RunNetsh(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(15000);
    }

    private static void EnsureHealthEndpoint(string entryRoot)
    {
        File.WriteAllText(
            Path.Combine(entryRoot, "adapanel-health.php"),
            "<?php header('Content-Type: text/plain; charset=utf-8'); echo 'AdaPanel-Webmail-OK';",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private async Task DeleteManagedSiteIfPresentAsync(CancellationToken cancellationToken)
    {
        try
        {
            await iisSiteService.DeleteSiteAsync(SiteName, cancellationToken);
        }
        catch (AppException exception) when (exception.MessageKey == "sites.site_not_found")
        {
            // İlk dağıtımda site yok.
        }
    }

    private Task<SiteDto> CreateSiteAsync(string physicalPath, int port, CancellationToken cancellationToken)
    {
        return iisSiteService.CreateSiteAsync(new CreateSiteRequest
        {
            Name = SiteName,
            PhysicalPath = physicalPath,
            ApplicationPoolName = $"AdaPanel.{SiteName}",
            HostName = string.Empty,
            IpAddress = "*",
            Port = port,
            Protocol = "http",
            AutoStart = true
        }, cancellationToken);
    }

    private static string GenerateDesKey()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        return string.Create(24, bytes, (span, source) =>
        {
            for (var i = 0; i < 24; i++)
            {
                span[i] = alphabet[source[i] % alphabet.Length];
            }
        });
    }

    private sealed record InstalledRoundcube(string InstallDir);

    private sealed class WebmailState
    {
        public string SiteName { get; set; } = string.Empty;

        public int Port { get; set; }

        public string EntryRoot { get; set; } = string.Empty;

        public string PhpComponentId { get; set; } = string.Empty;

        // Webmail'in HTTPS yapıldığı host'lar (webmail.<domain>). Doluysa OpenAsync https döner.
        public List<string> SecuredHosts { get; set; } = new();
    }
}
