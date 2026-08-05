using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Ftp;

public sealed partial class FtpService(
    IIisSiteService iisSiteService,
    JsonFileStore fileStore,
    IPanelPreferencesService panelPreferencesService,
    IOptions<PanelRuntimeOptions> runtimeOptions) : IFtpService
{
    private const string StatePath = "config/ftp-users.json";
    private const string TlsStatePath = "config/ftp-tls.json";
    private const string InfraStatePath = "config/ftp-infra.json";
    private const string FtpSiteName = "AdaPanel FTP";
    private const int FtpPort = 21;
    private const int PassivePortStart = 50000;
    private const int PassivePortEnd = 51000;
    private readonly string dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));

    public async Task<FtpServerStatusDto> EnsureServerAsync(CancellationToken cancellationToken = default)
    {
        await EnsureFtpInfrastructureAsync(false, cancellationToken);
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        return new FtpServerStatusDto
        {
            ServerAddress = preferences.ServerAddress,
            ControlPort = FtpPort,
            PassivePortStart = PassivePortStart,
            PassivePortEnd = PassivePortEnd,
            IsConfigured = true,
            StatusMessage = "ftp.server_ready"
        };
    }

    // M5 (profesyonel paneller gibi opsiyonel "yalnız güvenli FTP"): FTPS zorunlu mu? Varsayılan false = düz + FTPS (kilitlenme yok).
    public async Task<bool> GetRequireSslAsync(CancellationToken cancellationToken = default)
        => (await fileStore.ReadAsync(TlsStatePath, new FtpTlsState(), cancellationToken)).RequireSsl;

    public async Task<bool> SetRequireSslAsync(bool requireSsl, CancellationToken cancellationToken = default)
    {
        await fileStore.WriteAsync(TlsStatePath, new FtpTlsState { RequireSsl = requireSsl }, cancellationToken);
        await EnsureFtpInfrastructureAsync(true, cancellationToken); // SSL politikası değişti → tam yeniden kur (controlChannelPolicy 0/1 + FTPSVC restart)
        return requireSsl;
    }

    private sealed class FtpTlsState
    {
        public bool RequireSsl { get; set; }
    }

    // FTP altyapısı (IIS FTP sitesi + SSL + firewall) bir kez tam kurulduğunda işaretlenir → sonraki kullanıcı
    // eklemeleri ağır kurulumu + FTPSVC yeniden başlatmayı ATLAR (hızlı yol). SSL politikası değişimi force=true ile tam kurar.
    private sealed class FtpInfraState
    {
        public bool Configured { get; set; }
    }

    public async Task<IReadOnlyCollection<FtpUserDto>> GetFtpUsersAsync(CancellationToken cancellationToken = default)
    {
        var records = await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), cancellationToken);
        var list = new List<FtpUserDto>(records.Length);

        foreach (var record in records.OrderBy(item => item.UserName, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new FtpUserDto
            {
                UserName = record.UserName,
                HomePath = record.HomePath,
                SiteName = record.SiteName,
                CreatedAtUtc = record.CreatedAtUtc,
                IsEnabled = record.IsEnabled,
                ExistsOnSystem = await UserExistsAsync(record.UserName, cancellationToken),
                ServerAddress = Environment.MachineName,
                Port = FtpPort
            });
        }

        return list;
    }

    public async Task<FtpUserDto> CreateFtpUserAsync(CreateFtpUserRequest request, CancellationToken cancellationToken = default)
    {
        var userName = NormalizeUserName(request.UserName);
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Trim().Length < 8)
        {
            throw new AppException("ftp.password_too_short", 400, 400);
        }

        EnsureCommandSafePassword(request.Password);

        var homePath = await ResolveHomePathAsync(request, cancellationToken);
        Directory.CreateDirectory(homePath);
        await EnsureFtpInfrastructureAsync(false, cancellationToken);

        if (!await UserExistsAsync(userName, cancellationToken))
        {
            var createResult = await ProcessRunner.RunAsync("net.exe", $"user \"{userName}\" \"{request.Password}\" /add /y", cancellationToken: cancellationToken);
            if (!createResult.Success)
            {
                throw new AppException("ftp.user_create_failed", 500, 500, BuildCommandDetails(createResult));
            }
        }

        var aclResult = await ProcessRunner.RunAsync(
            "icacls.exe",
            $"\"{homePath}\" /grant \"{userName}:(OI)(CI)M\" /T /C",
            cancellationToken: cancellationToken);
        if (!aclResult.Success)
        {
            throw new AppException("ftp.acl_failed", 500, 500, BuildCommandDetails(aclResult));
        }

        await ConfigureUserHomeAsync(userName, homePath, cancellationToken);

        var record = new FtpUserState
        {
            UserName = userName,
            SiteName = request.SiteName.Trim(),
            HomePath = homePath,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        var records = (await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), cancellationToken)).ToList();
        var index = records.FindIndex(item => string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            records[index] = record;
        }
        else
        {
            records.Add(record);
        }

        await fileStore.WriteAsync(StatePath, records, cancellationToken);
        return new FtpUserDto
        {
            UserName = record.UserName,
            SiteName = record.SiteName,
            HomePath = record.HomePath,
            CreatedAtUtc = record.CreatedAtUtc,
            IsEnabled = true,
            ExistsOnSystem = true,
            ServerAddress = Environment.MachineName,
            Port = FtpPort
        };
    }

    public async Task SetFtpPasswordAsync(SetFtpPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var userName = NormalizeUserName(request.UserName);
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Trim().Length < 8)
        {
            throw new AppException("ftp.password_too_short", 400, 400);
        }

        EnsureCommandSafePassword(request.Password);

        if (!await UserExistsAsync(userName, cancellationToken))
        {
            throw new AppException("ftp.user_not_found", 404, 404);
        }

        // net user <ad> <parola> (/add YOK) parolayı değiştirir.
        var result = await ProcessRunner.RunAsync(
            "net.exe",
            $"user \"{userName}\" \"{request.Password}\" /y",
            cancellationToken: cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.password_change_failed", 500, 500, BuildCommandDetails(result));
        }
    }

    public async Task DeleteFtpUserAsync(string userName, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = NormalizeUserName(userName);
        await RemoveUserAuthorizationAsync(normalizedUserName, cancellationToken);

        if (await UserExistsAsync(normalizedUserName, cancellationToken))
        {
            var result = await ProcessRunner.RunAsync("net.exe", $"user \"{normalizedUserName}\" /delete", cancellationToken: cancellationToken);
            if (!result.Success)
            {
                // net.exe başarısızsa (lokalizasyon/kilit) PowerShell Remove-LocalUser ile dene.
                var psScript = $"$ErrorActionPreference='SilentlyContinue'; " +
                    $"Remove-LocalUser -Name '{normalizedUserName.Replace("'", "''")}'";
                await ProcessRunner.RunPowerShellAsync(psScript, cancellationToken);

                // Yine de duruyorsa gerçek hata; yoksa silinmiş kabul et (idempotent).
                if (await UserExistsAsync(normalizedUserName, cancellationToken))
                {
                    throw new AppException("ftp.user_delete_failed", 500, 500, BuildCommandDetails(result));
                }
            }
        }

        var records = (await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), cancellationToken))
            .Where(item => !string.Equals(item.UserName, normalizedUserName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        await fileStore.WriteAsync(StatePath, records, cancellationToken);
    }

    private async Task EnsureFtpInfrastructureAsync(bool forceReconfigure, CancellationToken cancellationToken)
    {
        // IIS FTP rolü kurulu değilse New-WebFtpSite ve ftpServer config bölümleri bulunmaz → script
        // "ftp.site_configuration_failed" ile patlar (canlı VPS bulgusu). Classic ASP/PHP gibi OTOMATİK KUR (atlama yok).
        await EnsureFtpFeatureInstalledAsync(cancellationToken);

        var ftpRoot = Path.Combine(dataRoot, "ftp-root");
        Directory.CreateDirectory(Path.Combine(ftpRoot, "LocalUser"));

        // HIZLI YOL: altyapı bir kez tam kurulduysa (bayrak) ve FTPSVC çalışıyorsa ağır yeniden-kurulumu + FTPSVC
        // yeniden başlatmayı ATLA → 2. ve sonraki FTP kullanıcısı eklemesi saniyelerce beklemeden, anında olur.
        // (SSL politikası değişimi gibi gerçekten gereken durumlar force=true ile tam yeniden kurar.)
        if (!forceReconfigure && await IsFtpInfrastructureReadyAsync(cancellationToken))
        {
            return;
        }

        // Pasif (PASV) mod: sunucu, veri kanalı için istemciye bir IP bildirir. Bu IP yanlış/iç IP olursa
        // istemci bağlanır, giriş yapar ama DİZİN LİSTESİ/transfer TAKILIR. Panelde tanımlı public IPv4'ü
        // FTP sitesine externalIp4Address olarak yazıyoruz; tanımlı değilse IIS kendi IP'sini kullanır.
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        var externalIp = preferences.ServerAddress?.Trim() ?? string.Empty;
        var externalIpLine = System.Net.IPAddress.TryParse(externalIp, out var parsedIp)
                && parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? $"Set-ItemProperty \"IIS:\\Sites\\$siteName\" -Name ftpServer.firewallSupport.externalIp4Address -Value '{EscapePowerShellLiteral(externalIp)}'"
            : "# sunucu IPv4 adresi panelde tanimli degil; pasif mod sunucunun kendi IP'sini kullanir";

        // M5 (profesyonel paneller gibi opsiyonel): FTPS zorunlu mu? 0=SslAllow (düz+FTPS, varsayılan), 1=SslRequire (yalnız FTPS).
        var requireSsl = (await fileStore.ReadAsync(TlsStatePath, new FtpTlsState(), cancellationToken)).RequireSsl;
        var policy = requireSsl ? 1 : 0;
        var policyName = requireSsl ? "SslRequire" : "SslAllow";

        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $siteName = '{{EscapePowerShellLiteral(FtpSiteName)}}'
            $root = '{{EscapePowerShellLiteral(ftpRoot)}}'
            if (-not (Test-Path "IIS:\Sites\$siteName")) {
              New-WebFtpSite -Name $siteName -Port {{FtpPort}} -PhysicalPath $root -Force | Out-Null
            }
            Set-ItemProperty "IIS:\Sites\$siteName" -Name serverAutoStart -Value $true
            # KRITIK KOK NEDEN (2026-06-21): FTP per-site ayarlari Set-WebConfigurationProperty -Location ile
            # YAZILAMAZ. ftpServer config <site> etiketinin ICINE gomulu; -Location ise <location> bloklarina
            # bakar -> sessizce "configuration object not found at location" UYARISI verip HICBIR SEYI
            # uygulamaz (script yine basarili doner). Sonuc: site IIS varsayilani SslRequire + sertifikasiz +
            # basicAuth kapali'da kalir = "534 Policy requires SSL" KILIDI. Dogru yol: IIS provider noktali
            # ozellik yolu (serverAutoStart'in calismasi gibi). Asagidaki TUM yazimlar provider yolu kullanir.
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.authentication.anonymousAuthentication.enabled -Value $false
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.authentication.basicAuthentication.enabled -Value $true
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.userIsolation.mode -Value 3
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.controlChannelPolicy -Value {{policy}}
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.dataChannelPolicy -Value {{policy}}
            # FTPS sertifikasi: yoksa self-signed olustur ve bagla. serverCertHash bos olursa IIS "AUTH TLS"i
            # reddeder; bagliyken FTPS calisir. controlChannelPolicy=0 ile duz baglanti da kabul -> kimse kilitlenmez.
            $ftpsCert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq 'AdaPanel FTPS' } | Select-Object -First 1
            if (-not $ftpsCert) {
              try { $ftpsCert = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME -FriendlyName 'AdaPanel FTPS' -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) } catch { $ftpsCert = $null }
            }
            if ($ftpsCert) {
              try {
                Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.serverCertHash -Value $ftpsCert.Thumbprint
                Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.ssl128 -Value $true
              } catch { }
            }
            # Pasif port araligi SUNUCU seviyesinde (location'siz) -> Set-WebConfigurationProperty dogru calisir.
            Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter 'system.ftpServer/firewallSupport' -Name lowDataChannelPort -Value {{PassivePortStart}}
            Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter 'system.ftpServer/firewallSupport' -Name highDataChannelPort -Value {{PassivePortEnd}}
            {{externalIpLine}}
            # SESSIZ BASARISIZLIK BIR DAHA OLMASIN: ayarin gercekten uygulandigini geri-okuyup dogrula.
            # NOT: bu geri-okuma yalnız UYARI verir, ASLA throw etmez. Bazı IIS sürümlerinde Get-ItemProperty
            # değeri farklı tipte döndürdüğü için (.Value boş/enum-adı) eski throw, Set basarili olsa bile
            # FTP'yi tümden bloke ediyordu (yanlis-pozitif). Set yukarida calisti; dogrulama bilgilendirme amacli.
            $applied = "$((Get-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.controlChannelPolicy -ErrorAction SilentlyContinue).Value)"
            if ($applied -ne '{{policy}}' -and $applied -ne '{{policyName}}') { Write-Warning "FTP SSL ayari dogrulanamadi (controlChannelPolicy=$applied, beklenen={{policy}})." }
            Start-WebSite -Name $siteName -ErrorAction SilentlyContinue
            # Pasif port aralığı (firewallSupport) ve SSL sertifikası değişikliklerinin uygulanması için
            # Microsoft FTP Service yeniden başlatılır (IIS bu ayarları canlı okumaz).
            Restart-Service FTPSVC -Force -ErrorAction SilentlyContinue
            if (-not (Get-NetFirewallRule -DisplayName 'AdaPanel FTP Control' -ErrorAction SilentlyContinue)) {
              New-NetFirewallRule -DisplayName 'AdaPanel FTP Control' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {{FtpPort}} -Profile Any | Out-Null
            }
            if (-not (Get-NetFirewallRule -DisplayName 'AdaPanel FTP Passive' -ErrorAction SilentlyContinue)) {
              New-NetFirewallRule -DisplayName 'AdaPanel FTP Passive' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {{PassivePortStart}}-{{PassivePortEnd}} -Profile Any | Out-Null
            }
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.site_configuration_failed", 500, 500, BuildCommandDetails(result));
        }

        // Tam kurulum başarılı → bayrağı işaretle ki sonraki kullanıcı eklemeleri hızlı yolu kullansın (servis restart yok).
        await fileStore.WriteAsync(InfraStatePath, new FtpInfraState { Configured = true }, cancellationToken);
    }

    // Altyapı hazır mı (hızlı kontrol): daha önce tam kuruldu (bayrak) VE FTPSVC çalışıyor mu. FTPSVC kontrolü
    // Get-Service ile yapılır (yavaş 'Import-Module WebAdministration' YOK → sub-saniye). Biri bile sağlanmazsa false →
    // çağıran tam kurulumu çalıştırır (kendini onarır). Böylece ağır betik + servis restart yalnız gerektiğinde çalışır.
    private async Task<bool> IsFtpInfrastructureReadyAsync(CancellationToken cancellationToken)
    {
        var state = await fileStore.ReadAsync(InfraStatePath, new FtpInfraState(), cancellationToken);
        if (!state.Configured)
        {
            return false;
        }

        var probe = await ProcessRunner.RunPowerShellAsync(
            "if ((Get-Service FTPSVC -ErrorAction SilentlyContinue).Status -eq 'Running') { 'up' } else { 'down' }",
            cancellationToken);
        return probe.Success && probe.StandardOutput.Contains("up", StringComparison.OrdinalIgnoreCase);
    }

    // IIS FTP sunucu rolü (FTPSVC servisi) yoksa OTOMATİK KURAR. Windows Server: Web-Ftp-Server (+ yönetim
    // araçları); Windows Client (10/11): IIS-FTPServer (-All ile alt özellik IIS-FTPSvc dahil). PHP/Classic ASP
    // on-demand kurulumuyla aynı desen — "atlama yok". Kurulamazsa net hata fırlatır (sessiz başarısızlık yok).
    private static async Task EnsureFtpFeatureInstalledAsync(CancellationToken cancellationToken)
    {
        const string probe = "if (Get-Service FTPSVC -ErrorAction SilentlyContinue) { 'present' } else { 'absent' }";

        var check = await ProcessRunner.RunPowerShellAsync(probe, cancellationToken);
        if (check.Success && check.StandardOutput.Contains("present", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        const string script =
            "if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) { " +
            "Install-WindowsFeature -Name Web-Ftp-Server -IncludeManagementTools -ErrorAction Stop | Out-Null } " +
            "else { Enable-WindowsOptionalFeature -Online -FeatureName IIS-FTPServer -All -NoRestart -ErrorAction Stop | Out-Null }";
        var install = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);

        var verify = await ProcessRunner.RunPowerShellAsync(probe, cancellationToken);
        if (!(verify.Success && verify.StandardOutput.Contains("present", StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException(
                "ftp.feature_install_failed",
                503,
                503,
                "IIS FTP rolu (Web-Ftp-Server/IIS-FTPServer) otomatik kurulamadi; FTPSVC servisi bulunamadi. "
                + BuildCommandDetails(install));
        }
    }

    /// <summary>
    /// <c>$userRoot</c> klasör bağlantısını GÜVENLE kaldıran PowerShell parçası.
    /// <para>
    /// NEDEN ÖZEL BİR YOL: <c>$userRoot</c> bir junction'dır ve HEDEFİ müşterinin site köküdür
    /// (bkz. <see cref="ResolveHomePathAsync"/>). Windows PowerShell 5.1'in bilinen reparse-point
    /// hatasında <c>Remove-Item -Recurse</c> bağlantıyı TAKİP EDER ve hedefteki dosyaları siler —
    /// yani bir FTP kullanıcısını silmek ya da ana dizinini değiştirmek, müşterinin bütün site
    /// dosyalarını götürebilir. Yedekten dönmeden geri alınamaz.
    /// </para>
    /// <para>
    /// <c>[IO.Directory]::Delete</c> bağlantıyı takip etmez; yalnızca bağlantının kendisini kaldırır.
    /// Bu yüzden önce "bu bir bağlantı mı" diye bakılır. Windows sürümünden bağımsız güvenlidir,
    /// dolayısıyla hatanın giderilmiş olduğu sürümlerde de doğru davranır.
    /// </para>
    /// </summary>
    private const string RemoveUserRootSnippet = """
        if (Test-Path -LiteralPath $userRoot) {
          $adaOge = Get-Item -LiteralPath $userRoot -Force
          if ($adaOge.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            [IO.Directory]::Delete($userRoot)
          } else {
            Remove-Item -LiteralPath $userRoot -Force -Recurse
          }
        }
        """;

    private async Task ConfigureUserHomeAsync(string userName, string homePath, CancellationToken cancellationToken)
    {
        var userRoot = Path.Combine(dataRoot, "ftp-root", "LocalUser", userName);
        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $siteName = '{{EscapePowerShellLiteral(FtpSiteName)}}'
            $userName = '{{EscapePowerShellLiteral(userName)}}'
            $userRoot = '{{EscapePowerShellLiteral(userRoot)}}'
            $homePath = '{{EscapePowerShellLiteral(homePath)}}'
            {{RemoveUserRootSnippet}}
            New-Item -ItemType Junction -Path $userRoot -Target $homePath | Out-Null
            Clear-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.ftpServer/security/authorization' -AtElement @{users=$userName} -ErrorAction SilentlyContinue
            Add-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.ftpServer/security/authorization' -Value @{accessType='Allow';users=$userName;permissions=3}
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.site_configuration_failed", 500, 500, BuildCommandDetails(result));
        }
    }

    private async Task RemoveUserAuthorizationAsync(string userName, CancellationToken cancellationToken)
    {
        var userRoot = Path.Combine(dataRoot, "ftp-root", "LocalUser", userName);
        var script = $$"""
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            $siteName = '{{EscapePowerShellLiteral(FtpSiteName)}}'
            $userName = '{{EscapePowerShellLiteral(userName)}}'
            $userRoot = '{{EscapePowerShellLiteral(userRoot)}}'
            if (Test-Path "IIS:\Sites\$siteName") {
              Clear-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.ftpServer/security/authorization' -AtElement @{users=$userName} -ErrorAction SilentlyContinue
            }
            {{RemoveUserRootSnippet}}
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.user_delete_failed", 500, 500, BuildCommandDetails(result));
        }
    }

    private async Task<string> ResolveHomePathAsync(CreateFtpUserRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.HomePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.HomePath.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(request.SiteName))
        {
            var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
            return string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                ? site.PhysicalPath
                : site.ApplicationRootPath;
        }

        return Path.Combine(dataRoot, "ftp", request.UserName.Trim());
    }

    private static async Task<bool> UserExistsAsync(string userName, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("net.exe", $"user \"{userName}\"", cancellationToken: cancellationToken);
        return result.Success;
    }

    // Parola net.exe argümanına çift tırnak içinde geçiyor. Bir '"' ya da SONDAKİ '\' tırnağı kırıp
    // ek argüman enjekte edebilir (kontrol karakterleri de). ProcessRunner shell kullanmasa da
    // (tam komut enjeksiyonu yok) bunları reddederek argüman enjeksiyonunu önlüyoruz.
    private static void EnsureCommandSafePassword(string password)
    {
        if (password.Contains('"') || password.EndsWith('\\') || password.Any(ch => ch < 0x20))
        {
            throw new AppException("ftp.invalid_password", 400, 400);
        }
    }

    private static string NormalizeUserName(string value)
    {
        var normalized = value.Trim();
        if (!UserNamePattern().IsMatch(normalized))
        {
            throw new AppException("ftp.invalid_user_name", 400, 400);
        }

        // FTP kullanıcısı Windows yerel hesabıdır; ad EN FAZLA 20 karakter olabilir (net user limiti).
        if (normalized.Length > 20)
        {
            throw new AppException("ftp.user_name_too_long", 400, 400);
        }

        return normalized;
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    private static string BuildCommandDetails(CommandResult result)
    {
        var details = $"""
            ExitCode={result.ExitCode}
            StandardOutput:
            {result.StandardOutput}
            StandardError:
            {result.StandardError}
            """;

        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private sealed class FtpUserState
    {
        public string UserName { get; set; } = string.Empty;

        public string SiteName { get; set; } = string.Empty;

        public string HomePath { get; set; } = string.Empty;

        public bool IsEnabled { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{1,31}$", RegexOptions.Compiled)]
    private static partial Regex UserNamePattern();
}
