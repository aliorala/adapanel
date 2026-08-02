using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Ssl;

public sealed class WinAcmeSslService(
    AdaPanelDbContext dbContext,
    IIisSiteService iisSiteService,
    IDnsService dnsService,
    JsonFileStore fileStore,
    IComponentBootstrapper componentBootstrapper,
    ILogger<WinAcmeSslService> logger) : ISslService
{
    private const string StatePath = "config/ssl.json";
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";

    public async Task<SslWorkspaceDto> GetSiteWorkspaceAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<SslCertificateDto>(), cancellationToken);

        return new SslWorkspaceDto
        {
            SiteName = site.Name,
            HostNames = site.Bindings
                .Where(binding => !string.IsNullOrWhiteSpace(binding.HostName))
                .Select(binding => binding.HostName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Certificates = states
                .Where(item => string.Equals(item.SiteName, site.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.NotAfterUtc)
                .ToArray(),
            HttpsRedirectEnabled = await IsHttpsRedirectEnabledAsync(site.Name, cancellationToken)
        };
    }

    public async Task<SslCertificateDto> IssueLetsEncryptAsync(IssueLetsEncryptRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);

        if (request.Wildcard)
        {
            return await IssueWildcardAsync(site, request, cancellationToken);
        }

        var hostName = ResolveHostName(site, request.HostName);
        var certificate = await TryIssueWithWinAcmeAsync(site, hostName, request.ContactEmail, request.UseStaging, cancellationToken)
            ?? CreateSelfSignedCertificate(hostName, site.Name);

        // İletişim e-postasını panel geneline kaydet ki mail sertifikası otomatik alınırken
        // (MailService) tekrar sorulmasın. Best-effort.
        if (!string.IsNullOrWhiteSpace(request.ContactEmail))
        {
            await fileStore.WriteAsync(
                Support.AcmeContactSettings.Path,
                new Support.AcmeContactSettings { Email = request.ContactEmail.Trim() },
                cancellationToken);
        }

        PersistCertificate(certificate);
        await EnsureHttpsBindingAsync(site.Name, hostName, 443, certificate, cancellationToken);

        // profesyonel paneller gibi: SSL kurulunca HTTP→HTTPS yönlendirmesi otomatik açılır (best-effort).
        await ApplyHttpsRedirectAsync(site.Name, true, cancellationToken);

        var dto = ToDto(site.Name, hostName, certificate, certificate.FriendlyName?.Contains("Let's Encrypt", StringComparison.OrdinalIgnoreCase) == true
            ? "letsencrypt"
            : "self-signed");

        await UpsertAsync(dto, cancellationToken);
        return dto;
    }

    // Domainsiz önizleme (nip.io): HIZLI self-signed HTTPS bağlaması — ACME beklemez, redirect'i değiştirmez.
    public async Task EnsurePreviewCertificateAsync(string siteName, string hostName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var certificate = CreateSelfSignedCertificate(hostName, site.Name);
        PersistCertificate(certificate);
        await EnsureHttpsBindingAsync(site.Name, hostName, 443, certificate, cancellationToken);
    }

    public async Task<SslCertificateDto> ImportCertificateAsync(ImportCertificateRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        var hostName = ResolveHostName(site, request.HostName);
        if (string.IsNullOrWhiteSpace(request.Base64Content))
        {
            throw new AppException("ssl.invalid_certificate_payload", 400, 400);
        }

        var raw = Convert.FromBase64String(request.Base64Content);
        var certificate = new X509Certificate2(
            raw,
            request.Password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);

        PersistCertificate(certificate);
        await EnsureHttpsBindingAsync(site.Name, hostName, request.Port <= 0 ? 443 : request.Port, certificate, cancellationToken);
        await ApplyHttpsRedirectAsync(site.Name, true, cancellationToken);

        var dto = ToDto(site.Name, hostName, certificate, "manual");
        await UpsertAsync(dto, cancellationToken);
        return dto;
    }

    public async Task<RenewAllResultDto> RenewAllAsync(CancellationToken cancellationToken = default)
    {
        var wacs = await ResolveWinAcmeExecutableAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(wacs))
        {
            return new RenewAllResultDto
            {
                RenewedCount = 0,
                Output = "win-acme kurulu degil."
            };
        }

        var result = await ProcessRunner.RunAsync(wacs, "--renew --baseuri https://acme-v02.api.letsencrypt.org/", cancellationToken: cancellationToken);
        return new RenewAllResultDto
        {
            RenewedCount = result.Success ? 1 : 0,
            Output = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput
        };
    }

    public async Task<SslWorkspaceDto> SetHttpsRedirectAsync(SetHttpsRedirectRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        await ApplyHttpsRedirectAsync(site.Name, request.Enabled, cancellationToken);
        return await GetSiteWorkspaceAsync(site.Name, cancellationToken);
    }

    // HTTP→HTTPS kalıcı (301) yönlendirme kuralını (URL Rewrite) ekler/kaldırır. ACME HTTP-01
    // challenge yolu (/.well-known/acme-challenge/) HARİÇ tutulur ki Let's Encrypt yenilemesi
    // bozulmasın. URL Rewrite modülü yoksa sessizce atlanır (SSL yine çalışır). Best-effort.
    private async Task ApplyHttpsRedirectAsync(string siteName, bool enabled, CancellationToken cancellationToken)
    {
        var site = siteName.Replace("'", "''");
        var script = enabled
            ? $$"""
                Import-Module WebAdministration -ErrorAction Stop
                if (-not (Get-WebGlobalModule | Where-Object { $_.Name -eq 'RewriteModule' })) { 'NOREWRITE'; return }
                $site = '{{site}}'; $f = 'system.webServer/rewrite/rules'; $name = 'AdaPanel-ForceHTTPS'
                Clear-WebConfiguration -pspath "IIS:\Sites\$site" -filter "$f/rule[@name='$name']" -ErrorAction SilentlyContinue
                Add-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter $f -name '.' -value @{name=$name; stopProcessing='True'}
                $r = "$f/rule[@name='$name']"
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/match" -name 'url' -value '(.*)'
                Add-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/conditions" -name '.' -value @{input='{HTTPS}'; pattern='off'}
                Add-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/conditions" -name '.' -value @{input='{REQUEST_URI}'; pattern='^/\.well-known/acme-challenge/'; negate='True'}
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'type' -value 'Redirect'
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'url' -value 'https://{HTTP_HOST}/{R:1}'
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'redirectType' -value 'Permanent'
                'OK'
                """
            : $$"""
                Import-Module WebAdministration -ErrorAction Stop
                $site = '{{site}}'
                Clear-WebConfiguration -pspath "IIS:\Sites\$site" -filter "system.webServer/rewrite/rules/rule[@name='AdaPanel-ForceHTTPS']" -ErrorAction SilentlyContinue
                'OK'
                """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);

        // URL Rewrite modülü yoksa panel bileşeni olarak OTOMATİK kur, sonra kuralı tekrar uygula.
        // (url-rewrite components.json'da default:true; ama kullanıcı ilk kurulumda önerilenleri
        // atlamışsa bile yönlendirme çalışsın.)
        if (enabled && result.Success && result.StandardOutput.Contains("NOREWRITE", StringComparison.Ordinal))
        {
            logger.LogInformation("URL Rewrite modülü kurulu değil; {Site} için otomatik kuruluyor (url-rewrite).", siteName);
            try
            {
                await componentBootstrapper.InstallAsync("url-rewrite", null, null, force: false, cancellationToken: cancellationToken);
                result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "url-rewrite otomatik kurulumu başarısız ({Site}).", siteName);
            }
        }

        if (!result.Success)
        {
            logger.LogWarning("HTTP->HTTPS yönlendirme {State} ayarlanamadı (site {Site}): {Error}",
                enabled ? "açma" : "kapatma", siteName, result.StandardError);
        }
        else if (result.StandardOutput.Contains("NOREWRITE", StringComparison.Ordinal))
        {
            logger.LogWarning("URL Rewrite modülü kurulu değil ve otomatik kurulamadı; {Site} için HTTP->HTTPS yönlendirme uygulanamadı.", siteName);
        }
    }

    private async Task<bool> IsHttpsRedirectEnabledAsync(string siteName, CancellationToken cancellationToken)
    {
        var site = siteName.Replace("'", "''");
        var script = $$"""
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            $site = '{{site}}'
            $rule = Get-WebConfiguration -pspath "IIS:\Sites\$site" -filter "system.webServer/rewrite/rules/rule[@name='AdaPanel-ForceHTTPS']" -ErrorAction SilentlyContinue
            if ($rule) { 'YES' } else { 'NO' }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        return result.Success && result.StandardOutput.Contains("YES", StringComparison.Ordinal);
    }

    public async Task<bool> TrySecureHostAsync(string siteName, string hostName, string contactEmail, CancellationToken cancellationToken = default)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);

            // 1) host'u kapsayan geçerli bir sertifika zaten varsa (ör. yüklenen wildcard) onu bağla.
            var existing = FindBestCertificate(hostName);
            if (existing is not null && existing.NotAfter > DateTime.Now)
            {
                await EnsureHttpsBindingAsync(site.Name, hostName, 443, existing, cancellationToken);
                return true;
            }

            // 2) yoksa Let's Encrypt dene. ÖNCE host DNS'te çözülüyor mu bak — çözülmüyorsa win-acme
            // ~120 sn takılır; hızlıca atla ki paylaşımlı site cert isteğini yavaşlatmasın.
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(hostName, cancellationToken);
                if (addresses.Length == 0)
                {
                    return false;
                }
            }
            catch
            {
                return false; // host çözülmüyor → Let's Encrypt denemeye gerek yok
            }

            var issued = await TryIssueWithWinAcmeAsync(site, hostName, contactEmail, false, cancellationToken);
            if (issued is null)
            {
                return false;
            }

            PersistCertificate(issued);
            await EnsureHttpsBindingAsync(site.Name, hostName, 443, issued, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Host '{Host}' güvenli (HTTPS) yapılamadı (best-effort).", hostName);
            return false;
        }
    }

    // --- Wildcard sertifika (DNS-01) — Plesk paritesi ---
    // win-acme "script" doğrulama eklentisiyle: panel, kendi Windows DNS'ine _acme-challenge TXT
    // kaydını ekleyen/silen bir PS scripti üretir; win-acme create/delete eylemleriyle çağırır.
    // CLI sözleşmesi (--source manual, --validation script, --dnsscript, create {Identifier}
    // {RecordName} {Token}) VPS'tekiyle AYNI binary'de (sha256 f4dc3b14...) yerelde doğrulandı.
    // ÖN KOŞUL: alan adının bölgesi panelin DNS'inde birincil bölge olarak yayında olmalı
    // (DNS ekranından); değilse script "bölge yok" der ve hata detayında görünür.

    private const string Dns01ScriptContent =
        """
        param(
            [Parameter(Position = 0)][string]$Action,
            [Parameter(Position = 1)][string]$Identifier,
            [Parameter(Position = 2)][string]$RecordName,
            [Parameter(Position = 3)][string]$Token
        )
        $ErrorActionPreference = 'Stop'
        $fqdn = $RecordName.TrimEnd('.')

        # En uzun eslesen birincil bolge (ornek: _acme-challenge.sub.example.com -> example.com).
        $zone = Get-DnsServerZone |
            Where-Object { -not $_.IsAutoCreated -and $_.ZoneType -eq 'Primary' -and ($fqdn -eq $_.ZoneName -or $fqdn.EndsWith('.' + $_.ZoneName)) } |
            Sort-Object { $_.ZoneName.Length } -Descending |
            Select-Object -First 1
        if (-not $zone) { throw "AdaPanel DNS-01: '$fqdn' icin panel DNS'inde birincil bolge bulunamadi. Once DNS ekranindan bolgeyi yayinlayin." }

        $name = if ($fqdn -eq $zone.ZoneName) { '@' } else { $fqdn.Substring(0, $fqdn.Length - $zone.ZoneName.Length - 1) }

        if ($Action -eq 'create') {
            Add-DnsServerResourceRecord -ZoneName $zone.ZoneName -Txt -Name $name -DescriptiveText $Token -TimeToLive ([TimeSpan]::FromSeconds(60)) | Out-Null
        }
        elseif ($Action -eq 'delete') {
            Get-DnsServerResourceRecord -ZoneName $zone.ZoneName -Name $name -RRType 'TXT' -ErrorAction SilentlyContinue |
                Where-Object { $_.RecordData.DescriptiveText -eq $Token } |
                Remove-DnsServerResourceRecord -ZoneName $zone.ZoneName -Force -ErrorAction SilentlyContinue
        }
        else { throw "AdaPanel DNS-01: bilinmeyen eylem '$Action'." }
        """;

    // DNS-01 ÖN KONTROLÜ (preflight). Neden gerekli: win-acme her deneme için ~5 dk boyunca
    // "No TXT records found / retry" döngüsüne girer, sonunda Let's Encrypt "query timed out"
    // der ve bu BAŞARISIZ doğrulama LE hesap limitine yazılır. Oysa kayıt panelin DNS'ine
    // yazılmıştır; sorun kaydın DIŞARIDAN görünmemesidir. Bunu önceden, 1 dakikanın altında,
    // gerçek nedeni söyleyerek tespit ediyoruz:
    //   1) bölge panelin DNS'inde birincil olarak var mı,
    //   2) alan adının NS delegasyonu (halka açık çözümleyiciden) var mı,
    //   3) o NS'ler gerçekten bu sunucunun yazdığı TXT'yi yanıtlıyor mu (53/UDP açık + servis ayakta).
    private const string Dns01PreflightScript =
        """
        $ErrorActionPreference = 'Continue'
        $result = [ordered]@{
            zone = ''
            nsNames = @()
            nsIps = @()
            localIps = @()
            publicVisible = $false
            authoritativeVisible = $false
            notes = @()
        }

        try {
            $zone = Get-DnsServerZone -ErrorAction Stop |
                Where-Object { -not $_.IsAutoCreated -and $_.ZoneType -eq 'Primary' -and ($domain -eq $_.ZoneName -or $domain.EndsWith('.' + $_.ZoneName)) } |
                Sort-Object { $_.ZoneName.Length } -Descending |
                Select-Object -First 1
            if ($zone) { $result.zone = $zone.ZoneName }
        }
        catch { $result.notes += "dns-role: $($_.Exception.Message)" }

        $result.localIps = @(
            Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
                Select-Object -ExpandProperty IPAddress
        )

        foreach ($resolver in @('1.1.1.1', '8.8.8.8')) {
            if ($result.nsNames.Count -gt 0) { break }
            try {
                $ns = Resolve-DnsName -Name $domain -Type NS -Server $resolver -DnsOnly -ErrorAction Stop |
                    Where-Object { $_.NameHost } |
                    Select-Object -ExpandProperty NameHost -Unique
                if ($ns) { $result.nsNames = @($ns) }
            }
            catch { $result.notes += "ns-query($resolver): $($_.Exception.Message)" }
        }

        foreach ($name in $result.nsNames) {
            try {
                $ips = Resolve-DnsName -Name $name -Type A -Server '1.1.1.1' -DnsOnly -ErrorAction Stop |
                    Where-Object { $_.IPAddress } |
                    Select-Object -ExpandProperty IPAddress -Unique
                foreach ($ip in $ips) { if ($result.nsIps -notcontains $ip) { $result.nsIps += $ip } }
            }
            catch { $result.notes += "ns-resolve($name): $($_.Exception.Message)" }
        }

        if ($result.zone) {
            $token = [guid]::NewGuid().ToString('N')
            $label = '_adapanel-acme-check'
            $probeFqdn = "$label.$domain"
            $relative = if ($domain -eq $result.zone) {
                $label
            } else {
                $label + '.' + $domain.Substring(0, $domain.Length - $result.zone.Length - 1)
            }

            $written = $false
            try {
                Add-DnsServerResourceRecord -ZoneName $result.zone -Txt -Name $relative -DescriptiveText $token -TimeToLive ([TimeSpan]::FromSeconds(30)) -ErrorAction Stop | Out-Null
                $written = $true
            }
            catch { $result.notes += "probe-write: $($_.Exception.Message)" }

            if ($written) {
                foreach ($ip in $result.nsIps) {
                    try {
                        $answer = Resolve-DnsName -Name $probeFqdn -Type TXT -Server $ip -DnsOnly -ErrorAction Stop
                        if (@($answer | ForEach-Object { $_.Strings }) -contains $token) {
                            $result.authoritativeVisible = $true
                            break
                        }
                    }
                    catch { $result.notes += "auth-query($ip): $($_.Exception.Message)" }
                }

                for ($i = 0; $i -lt 3; $i++) {
                    try {
                        $answer = Resolve-DnsName -Name $probeFqdn -Type TXT -Server '1.1.1.1' -DnsOnly -ErrorAction Stop
                        if (@($answer | ForEach-Object { $_.Strings }) -contains $token) {
                            $result.publicVisible = $true
                            break
                        }
                    }
                    catch { }
                    Start-Sleep -Seconds 2
                }

                try {
                    Get-DnsServerResourceRecord -ZoneName $result.zone -Name $relative -RRType 'TXT' -ErrorAction SilentlyContinue |
                        Where-Object { $_.RecordData.DescriptiveText -eq $token } |
                        Remove-DnsServerResourceRecord -ZoneName $result.zone -Force -ErrorAction SilentlyContinue
                }
                catch { $result.notes += "probe-cleanup: $($_.Exception.Message)" }
            }
        }

        $result | ConvertTo-Json -Depth 4 -Compress
        """;

    private sealed class Dns01Preflight
    {
        public string Zone { get; init; } = string.Empty;

        public string[] NsNames { get; init; } = [];

        public string[] NsIps { get; init; } = [];

        public string[] LocalIps { get; init; } = [];

        public bool PublicVisible { get; init; }

        public bool AuthoritativeVisible { get; init; }

        public string[] Notes { get; init; } = [];
    }

    private async Task EnsureDns01ResolvableAsync(string domain, CancellationToken cancellationToken)
    {
        // Alan adı doğrudan PowerShell'e gömüldüğü için katı doğrulama (komut enjeksiyonu yok).
        if (!System.Text.RegularExpressions.Regex.IsMatch(domain, "^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$"))
        {
            throw new AppException("ssl.host_name_required", 400, 400);
        }

        var script = $"$domain = '{domain}'\r\n{Dns01PreflightScript}";
        var run = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);

        Dns01Preflight? preflight = null;
        try
        {
            var json = run.StandardOutput.Trim();
            var start = json.IndexOf('{');
            if (start >= 0)
            {
                preflight = System.Text.Json.JsonSerializer.Deserialize<Dns01Preflight>(
                    json[start..],
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DNS-01 ön kontrol çıktısı ayrıştırılamadı; doğrulama yine denenecek.");
        }

        if (preflight is null)
        {
            // Ön kontrol çalıştırılamadıysa (ör. DnsServer/DnsClient modülü yok) yolu KAPATMA:
            // win-acme yine denenir, hata detayı kullanıcıya döner.
            logger.LogWarning(
                "DNS-01 ön kontrolü yapılamadı (çıkış {ExitCode}). Doğrulama doğrudan deneniyor. {Error}",
                run.ExitCode,
                run.StandardError);
            return;
        }

        var detail = new StringBuilder()
            .Append("domain=").Append(domain)
            .Append("; zone=").Append(string.IsNullOrWhiteSpace(preflight.Zone) ? "(none)" : preflight.Zone)
            .Append("; ns=").Append(preflight.NsNames.Length == 0 ? "(none)" : string.Join(",", preflight.NsNames))
            .Append("; nsIp=").Append(preflight.NsIps.Length == 0 ? "(unresolved)" : string.Join(",", preflight.NsIps))
            .Append("; serverIp=").Append(string.Join(",", preflight.LocalIps))
            .Append("; authoritativeAnswer=").Append(preflight.AuthoritativeVisible)
            .Append("; publicAnswer=").Append(preflight.PublicVisible);

        if (preflight.Notes.Length > 0)
        {
            detail.Append("; notes=").Append(string.Join(" | ", preflight.Notes.Take(6)));
        }

        var details = detail.ToString();

        if (string.IsNullOrWhiteSpace(preflight.Zone))
        {
            throw new AppException("ssl.dns01_zone_missing", 409, 409, details);
        }

        if (preflight.NsNames.Length == 0)
        {
            throw new AppException("ssl.dns01_no_delegation", 409, 409, details);
        }

        if (preflight.PublicVisible || preflight.AuthoritativeVisible)
        {
            if (!preflight.PublicVisible)
            {
                logger.LogInformation(
                    "DNS-01 ön kontrolü: yetkili sunucu TXT'yi yanıtlıyor ama genel çözümleyici henüz görmedi (yayılma). Devam ediliyor. {Details}",
                    details);
            }

            return;
        }

        // Kayıt panelin DNS'ine yazıldı, delegasyon var, ama delege edilen NS'ler kaydı YANITLAMIYOR.
        // BU NOKTADA EN SIK GERÇEK NEDEN (2026-07-23'te bu sunucuda yaşandı): DNS servisi çalışıyor
        // ama genel IP'ye bind olmamış. Onu ayırt edip kullanıcıyı tek tık onarıma yönlendiriyoruz;
        // aksi halde "NS kayıtlarınız yanlış" der ve kullanıcı boşuna registrar'da arar.
        try
        {
            var dnsHealth = await dnsService.GetServerHealthAsync(cancellationToken);
            if (dnsHealth.NeedsRepair)
            {
                throw new AppException("ssl.dns01_not_listening", 409, 409, $"{details}; dns={dnsHealth.Details}");
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DNS sunucu bind kontrolü yapılamadı; genel DNS-01 hatası dönülüyor.");
        }

        throw new AppException("ssl.dns01_ns_unreachable", 409, 409, details);
    }

    private async Task<SslCertificateDto> IssueWildcardAsync(
        AdaPanel.Core.Dtos.Sites.SiteDto site,
        IssueLetsEncryptRequest request,
        CancellationToken cancellationToken)
    {
        // Wildcard'da self-signed'a SESSİZ düşme YOK: kullanıcı bilinçli DNS-01 istedi, hata gerçek nedeniyle döner.
        // Apex türetme: "*." ve baştaki "www." atılır (kullanıcı listeden www'lu hostu seçmiş olabilir).
        var domain = ResolveHostName(site, request.HostName).TrimStart('*').TrimStart('.').ToLowerInvariant();
        if (domain.StartsWith("www.", StringComparison.Ordinal))
        {
            domain = domain[4..];
        }
        if (string.IsNullOrWhiteSpace(request.ContactEmail))
        {
            throw new AppException("ssl.contact_email_required", 400, 400);
        }

        var wacs = await ResolveWinAcmeExecutableAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(wacs))
        {
            throw new AppException("ssl.winacme_missing", 409, 409);
        }

        // Doğrulamayı denemeden ÖNCE zincirin dışarıdan çalıştığını kanıtla (bkz. EnsureDns01ResolvableAsync).
        await EnsureDns01ResolvableAsync(domain, cancellationToken);

        var scriptPath = Path.Combine(fileStore.EnsureDirectory("tools"), "acme-dns01.ps1");
        await File.WriteAllTextAsync(scriptPath, Dns01ScriptContent, cancellationToken);

        var baseUri = request.UseStaging
            ? "https://acme-staging-v02.api.letsencrypt.org/"
            : "https://acme-v02.api.letsencrypt.org/";

        var args =
            $"--source manual --host *.{domain},{domain} " +
            $"--validation script --dnsscript \"{scriptPath}\" --dnsscriptparallelism 0 " +
            $"--installation iis --installationsiteid {site.Id} " +
            $"--emailaddress {request.ContactEmail.Trim()} --accepttos --baseuri {baseUri}";

        // DNS-01 turları (kayıt yazma + yetkili sunucu doğrulaması) HTTP-01'den uzun sürebilir.
        var result = await ProcessRunner.RunAsync(wacs, args, cancellationToken: cancellationToken, timeout: TimeSpan.FromMinutes(10));
        if (!result.Success)
        {
            var detail = (result.StandardError + "\r\n" + result.StandardOutput).Trim();
            throw new AppException("ssl.wildcard_failed", 502, 502, detail.Length > 2000 ? detail[^2000..] : detail);
        }

        var certificate = FindBestCertificate($"*.{domain}")
            ?? throw new AppException("ssl.wildcard_failed", 502, 502, "win-acme succeeded but the wildcard certificate was not found in the store.");

        PersistCertificate(certificate);
        await ApplyHttpsRedirectAsync(site.Name, true, cancellationToken);

        var dto = ToDto(site.Name, $"*.{domain}", certificate, "letsencrypt");
        await UpsertAsync(dto, cancellationToken);
        return dto;
    }

    private async Task<X509Certificate2?> TryIssueWithWinAcmeAsync(
        AdaPanel.Core.Dtos.Sites.SiteDto site,
        string hostName,
        string contactEmail,
        bool useStaging,
        CancellationToken cancellationToken)
    {
        var wacs = await ResolveWinAcmeExecutableAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(wacs) || string.IsNullOrWhiteSpace(contactEmail))
        {
            return null;
        }

        var baseUri = useStaging
            ? "https://acme-staging-v02.api.letsencrypt.org/"
            : "https://acme-v02.api.letsencrypt.org/";

        var before = FindBestCertificate(hostName);
        var args = $"--target iis --siteid {site.Id} --host {hostName} --installation iis --emailaddress {contactEmail} --accepttos --baseuri {baseUri}";
        // HTTP-01 doğrulaması domain sunucuya işaret etmiyorsa/80 kapalıysa win-acme uzun
        // süre takılabilir. 120 sn sonra iptal et ki istek sonsuza kadar dönmesin (self-signed'a düşer).
        var result = await ProcessRunner.RunAsync(wacs, args, cancellationToken: cancellationToken, timeout: TimeSpan.FromSeconds(120));
        if (!result.Success)
        {
            return null;
        }

        var after = FindBestCertificate(hostName);
        return after?.Thumbprint != before?.Thumbprint ? after : before;
    }

    private async Task<string> ResolveWinAcmeExecutableAsync(CancellationToken cancellationToken)
    {
        var component = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == "win-acme" && item.Status == ComponentInstallStatus.Installed, cancellationToken);

        if (component is null || string.IsNullOrWhiteSpace(component.InstallDir) || !Directory.Exists(component.InstallDir))
        {
            return string.Empty;
        }

        return Directory.GetFiles(component.InstallDir, "wacs.exe", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
    }

    private static string ResolveHostName(AdaPanel.Core.Dtos.Sites.SiteDto site, string? requestedHostName)
    {
        var hostName = string.IsNullOrWhiteSpace(requestedHostName)
            ? site.Bindings.FirstOrDefault(binding => !string.IsNullOrWhiteSpace(binding.HostName))?.HostName
            : requestedHostName.Trim();

        if (string.IsNullOrWhiteSpace(hostName))
        {
            throw new AppException("ssl.host_name_required", 400, 400);
        }

        return hostName;
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string hostName, string siteName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(hostName);
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var export = certificate.Export(X509ContentType.Pfx);
        var imported = new X509Certificate2(export, string.Empty, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
        imported.FriendlyName = $"AdaPanel SelfSigned {siteName}";
        return imported;
    }

    // IIS/HTTP.sys YALNIZCA LocalMachine\My deposundaki sertifikayı bağlayabilir. Bu yüzden
    // CurrentUser'a düşmeyiz (binding "bağlanamadı" verirdi); LocalMachine başarısızsa hatayı
    // açıkça yükseltiriz. Aynı parmak izi zaten varsa tekrar eklemeyiz ki özel anahtarlı
    // kopyanın üzerine anahtarsız kopya yazılmasın.
    private static void PersistCertificate(X509Certificate2 certificate)
    {
        using var machineStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        machineStore.Open(OpenFlags.ReadWrite);

        var alreadyPresent = !string.IsNullOrWhiteSpace(certificate.Thumbprint) &&
            machineStore.Certificates
                .Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false)
                .Count > 0;

        if (!alreadyPresent)
        {
            machineStore.Add(certificate);
        }
    }

    private static X509Certificate2? FindBestCertificate(string hostName)
    {
        // win-acme, IIS kurulumunda LE sertifikasını LocalMachine\WebHosting deposuna koyar
        // (My DEĞİL). Önce WebHosting taranır; bulunamazsa My. Özel anahtarlı (bağlanabilir)
        // sertifika tercih edilir, yoksa son çare anahtarsız eşleşme.
        var stores = new (string Name, StoreLocation Location)[]
        {
            ("WebHosting", StoreLocation.LocalMachine),
            ("My", StoreLocation.LocalMachine),
            ("My", StoreLocation.CurrentUser)
        };

        X509Certificate2? fallback = null;
        foreach (var (name, location) in stores)
        {
            X509Store store;
            try
            {
                store = new X509Store(name, location);
                store.Open(OpenFlags.ReadOnly);
            }
            catch
            {
                continue;
            }

            using (store)
            {
                var matches = store.Certificates
                    .OfType<X509Certificate2>()
                    .Where(cert => cert.Subject.Contains($"CN={hostName}", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(cert => cert.NotAfter)
                    .ToList();

                var withKey = matches.FirstOrDefault(cert => cert.HasPrivateKey);
                if (withKey is not null)
                {
                    return withKey;
                }

                fallback ??= matches.FirstOrDefault();
            }
        }

        return fallback;
    }

    // IIS HTTPS binding'i artık PowerShell WebAdministration ile yapılır (New-WebBinding -SslFlags 1
    // + AddSslCertificate). Microsoft.Web.Administration reflection bazı IIS sürümlerinde SNI
    // sertifikası bağlamayı "bağlanamadı" ile reddediyordu; PowerShell yöntemi güvenilir çalışır.
    private async Task EnsureHttpsBindingAsync(string siteName, string hostName, int port, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var thumbprint = certificate.Thumbprint ?? string.Empty;
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new AppException("ssl.binding_failed", 500, 500, "Sertifika parmak izi (thumbprint) yok.");
        }

        var site = siteName.Replace("'", "''");
        var dn = (hostName ?? string.Empty).Replace("'", "''");
        var thumb = thumbprint.Replace("'", "''");

        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $ErrorActionPreference = 'Stop'
            $site = '{{site}}'; $dn = '{{dn}}'; $port = {{port}}; $thumb = '{{thumb}}'
            Get-WebBinding -Name $site -Protocol https -Port $port -HostHeader $dn -ErrorAction SilentlyContinue |
              Remove-WebBinding -ErrorAction SilentlyContinue
            if ($dn) {
              New-WebBinding -Name $site -Protocol https -Port $port -HostHeader $dn -SslFlags 1
              $b = Get-WebBinding -Name $site -Protocol https -Port $port -HostHeader $dn
            } else {
              New-WebBinding -Name $site -Protocol https -Port $port
              $b = Get-WebBinding -Name $site -Protocol https -Port $port
            }
            # Sertifika hangi depodaysa ORADAN bağla. win-acme LE sertifikasını WebHosting'e koyar;
            # self-signed/import ise My'da olur. Yanlış depo "güvenli değil" / boş binding'e yol açar.
            $store = 'My'
            if (-not (Test-Path "Cert:\LocalMachine\My\$thumb") -and (Test-Path "Cert:\LocalMachine\WebHosting\$thumb")) {
              $store = 'WebHosting'
            }
            $b.AddSslCertificate($thumb, $store)
            'OK'
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException(
                "ssl.binding_failed",
                500,
                500,
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        }
    }

    public async Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        await fileStore.UpdateAsync(StatePath, Array.Empty<SslCertificateDto>(), existing => existing
            .Where(item => !string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase))
            .ToArray(), cancellationToken);
    }

    private async Task UpsertAsync(SslCertificateDto dto, CancellationToken cancellationToken)
    {
        // Atomik oku-değiştir-yaz (eşzamanlı SSL işlemlerinde kayıp güncelleme olmasın).
        await fileStore.UpdateAsync(StatePath, Array.Empty<SslCertificateDto>(), existing =>
        {
            var states = existing.ToList();
            var index = states.FindIndex(item =>
                string.Equals(item.SiteName, dto.SiteName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.HostName, dto.HostName, StringComparison.OrdinalIgnoreCase) &&
                item.Port == dto.Port);

            if (index >= 0)
            {
                states[index] = dto;
            }
            else
            {
                states.Add(dto);
            }

            return states.ToArray();
        }, cancellationToken);
    }

    private static SslCertificateDto ToDto(string siteName, string hostName, X509Certificate2 certificate, string source)
    {
        return new SslCertificateDto
        {
            SiteName = siteName,
            HostName = hostName,
            Thumbprint = certificate.Thumbprint ?? string.Empty,
            FriendlyName = certificate.FriendlyName ?? string.Empty,
            Subject = certificate.Subject,
            NotAfterUtc = certificate.NotAfter.ToUniversalTime(),
            Source = source,
            IsHttpsBindingPresent = true,
            Port = 443
        };
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

    private static object GetPropertyObject(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            return property.GetValue(target)
                ?? throw new AppException("sites.iis_not_available", 503, 503);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static object? FindByName(object collection, string name)
    {
        return Enumerate(collection).FirstOrDefault(item =>
            string.Equals(GetStringProperty(item, "Name"), name, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<object> Enumerate(object collection)
    {
        return collection is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object>()
            : [];
    }

    private static string GetStringProperty(object target, string propertyName)
    {
        return GetPropertyObject(target, propertyName).ToString() ?? string.Empty;
    }

    private static void SetPropertyValue(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            property.SetValue(target, value);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static object InvokeMethod(object target, string methodName, params object[] arguments)
    {
        try
        {
            return target.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                null,
                target,
                arguments)
                ?? throw new InvalidOperationException($"IIS method '{methodName}' returned no result.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static AppException MapIisException(Exception exception)
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

        return CreateDiagnosticException("ssl.binding_failed", 500, exception);
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
}
