using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.PanelAccess;
using AdaPanel.Core.Dtos.Proxy;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Health;

/// <summary>
/// Paneli yöneticinin KENDİ alan adında, GEÇERLİ bir sertifikayla dışarıya açar.
/// <para>
/// Sorun: panel Kestrel üzerinde kendinden imzalı bir sertifikayla çalışır. Tarayıcı uyarıyı
/// "devam et" ile geçebilir; panele /mcp üzerinden kendi altyapısından bağlanan yapay zeka
/// istemcileri GEÇEMEZ — el sıkışma reddedilir ve kullanıcı hiçbir açıklama görmez.
/// </para>
/// <para>
/// Çözüm: IIS'te ters vekil sitesi (443 → yerel panel portu) + Let's Encrypt sertifikası. Bu
/// servis yeni bir ters vekil/ACME altyapısı YAZMAZ; panelde zaten çalışan
/// <see cref="IReverseProxyService"/> ve <see cref="ISslService"/> servislerini sırayla çağırır.
/// </para>
/// <para>
/// KİLİT KURALI: <c>IConcurrencyLock</c> bu projede statik ve yeniden girişe kapalıdır. Çağırdığımız
/// servislerin (IIS site yazımı, ters vekil yazımı) her biri kendi içinde kilit alır; bu yüzden bu
/// sınıf kilidi ASLA almaz ve çağrıları peş peşe (iç içe değil) yapar.
/// </para>
/// <para>
/// SIRA ÖNEMLİ (bilinçli seçim, bkz. SetupAsync): önce sertifika, SONRA ters vekil. Ters vekilin
/// kök kuralı (<c>^(.*)$ stopProcessing</c>) her isteği panele iletir; kural önce konsaydı
/// Let's Encrypt'in HTTP-01 doğrulama dosyası (<c>/.well-known/acme-challenge/…</c>) IIS yerine
/// panele giderdi ve doğrulama daima başarısız olurdu. Aynı sebeple ters vekilden önce, yenileme
/// için kalıcı bir "acme-challenge hariç" kuralı eklenir.
/// </para>
/// </summary>
public sealed partial class PanelPublicAccessService(
    IIisSiteService iisSiteService,
    IReverseProxyService reverseProxyService,
    ISslService sslService,
    IPanelPreferencesService panelPreferencesService,
    JsonFileStore fileStore,
    IFirewallService firewallService,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<PanelPublicAccessService> logger) : IPanelPublicAccessService
{
    /// <summary>
    /// Ters vekil sitesinin SABİT adı. Alan adı site adı olarak KULLANILMAZ: IIisSiteService.CreateSiteAsync
    /// aynı adlı mevcut siteyi silip yeniden oluşturur; kullanıcının aynı adı taşıyan gerçek sitesi
    /// yok olurdu. Sabit ve panele ait bir ad bu çakışmayı tümüyle ortadan kaldırır.
    /// </summary>
    private const string ProxySiteName = "AdaPanel.PanelAccess";

    private const string PreferencesPath = "config/panel-preferences.json";

    /// <summary>Kök vekil kuralından ÖNCE çalışıp ACME doğrulama dosyasını IIS'e bırakan kural.</summary>
    private const string AcmeExclusionRuleName = "AdaPanel.PanelAccess.AcmeChallenge";

    /// <summary>Her dış ölçüm kısa timeout'lu: durum sorgusu bir isteği dakikalarca dondurmamalı.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    // --- Durum ---

    public async Task<PanelAccessStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        var hostName = (preferences.PanelHostName ?? string.Empty).Trim();

        var report = new PanelAccessStatusDto
        {
            HostName = hostName,
            IsConfigured = hostName.Length > 0,
            SiteName = ProxySiteName,
            BackendUrl = ResolveBackendUrl(),
            CheckedAtUtc = DateTime.UtcNow
        };

        if (!report.IsConfigured)
        {
            // Alan adı yoksa ölçülecek bir şey de yok. "ok" demek yanıltıcı, "fail" demek yanlış
            // olurdu: özellik kapalı, bozuk değil.
            report.Steps = BuildUnknownSteps("Panel host name is not set; public access is disabled.");
            report.OverallStatus = "unknown";
            return report;
        }

        report.PanelUrl = $"https://{hostName}/";
        report.McpEndpoint = $"https://{hostName}/mcp";

        var steps = new List<PanelAccessStepDto>();

        var (dnsStep, _) = await CheckDnsAsync(hostName, preferences.ServerAddress, cancellationToken);
        steps.Add(dnsStep);

        var (siteStep, site) = await CheckSiteAsync(hostName, cancellationToken);
        steps.Add(siteStep);

        var (certificateStep, certificateInfo) = await ProbeCertificateAsync(hostName, cancellationToken);
        steps.Add(certificateStep);
        report.CertificateSource = certificateInfo.Source;
        report.CertificateIsSelfSigned = certificateInfo.IsSelfSigned;
        report.CertificateDaysLeft = certificateInfo.DaysLeft;

        var (proxyStep, renewalStep) = await CheckProxyAsync(site, report.BackendUrl, cancellationToken);
        steps.Add(renewalStep);
        steps.Add(proxyStep);

        steps.Add(await ProbeExternalAsync(hostName, cancellationToken));

        report.Steps = steps;
        report.OverallStatus = CombineStatus(steps);
        return report;
    }

    // --- Kurulum ---

    public async Task<PanelAccessStatusDto> SetupAsync(SetupPanelAccessRequest request, CancellationToken cancellationToken = default)
    {
        var hostName = NormalizeHostName(request.HostName);
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        var backendUrl = ResolveBackendUrl();

        var report = new PanelAccessStatusDto
        {
            HostName = hostName,
            IsConfigured = true,
            SiteName = ProxySiteName,
            BackendUrl = backendUrl,
            PanelUrl = $"https://{hostName}/",
            McpEndpoint = $"https://{hostName}/mcp",
            CheckedAtUtc = DateTime.UtcNow
        };

        var steps = new List<PanelAccessStepDto>();

        // 1) DNS — alan adı bu sunucuya çözülmüyorsa Let's Encrypt ZATEN başarısız olur (üstelik
        //    başarısız doğrulama LE hesap limitine yazılır). Önce burada durur, net hata veririz.
        var (dnsStep, dnsMatches) = await CheckDnsAsync(hostName, preferences.ServerAddress, cancellationToken);
        steps.Add(dnsStep);
        if (!dnsMatches)
        {
            report.Steps = Pad(steps, "Skipped: the DNS step failed, so no further change was made.");
            report.OverallStatus = "fail";
            throw new AppException(
                dnsStep.Value.Length == 0 ? "panelAccess.dns_not_resolved" : "panelAccess.dns_mismatch",
                409,
                409,
                dnsStep.Detail);
        }

        // 2) IIS sitesi — sabit adlı ters vekil sitesi. VARSA yeniden oluşturulmaz (bu, 443
        //    bağlamasını ve sertifikayı silerdi); yalnız alan adı bağlaması güncellenir.
        var (siteStep, site) = await EnsureSiteAsync(hostName, preferences.DefaultSiteRoot, cancellationToken);
        steps.Add(siteStep);
        if (siteStep.Status == "fail" || site is null)
        {
            report.Steps = Pad(steps, "Skipped: the IIS site step failed, so no further change was made.");
            report.OverallStatus = "fail";
            await SaveHostNameAsync(hostName, cancellationToken);
            return report;
        }

        // 2b) Güvenlik duvarı: 443 açık değilse kurulumun geri kalanı doğru yapılsa bile panel
        //     dışarıdan erişilemez ve bu ancak son adımda "bağlantı zaman aşımı" olarak görünür.
        //     Site oluşturma yalnız kendi oluşturduğu bağlama portunu (80) açıyor; HTTPS portunu
        //     kimse açmıyordu. Başarısızlık ölümcül değil: kural zaten varsa ya da elle yönetiliyorsa
        //     kurulum devam etmeli, sonucu son adım zaten ölçüyor.
        try
        {
            await firewallService.AllowPortAsync(
                new UpdateFirewallRuleRequest { RuleName = "AdaPanel Panel Access HTTPS", Port = 443, Protocol = "TCP" },
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "443 portu için güvenlik duvarı kuralı eklenemedi; dışarıdan erişim adımı bunu gösterecek.");
        }

        // 3) Sertifika — ters vekilden ÖNCE (sınıf açıklamasındaki sıra gerekçesi).
        steps.Add(await IssueCertificateAsync(hostName, request, cancellationToken));

        // 4) Yenileme muafiyeti — kök vekil kuralı eklenmeden ÖNCE yazılır ki kural listesinde
        //    ondan önce gelsin (AddRewriteRuleAsync vekil kuralı varsa onun ÖNÜNE ekler, yoksa sona).
        steps.Add(await EnsureAcmeExclusionAsync(cancellationToken));

        // 5) Ters vekil — mevcut servisi çağırır (yeniden yazılmaz).
        steps.Add(await EnableProxyAsync(backendUrl, cancellationToken));

        // 6) Dışarıdan gerçek doğrulama: yapay zeka istemcisinin yapacağı şeyin aynısı —
        //    zincir doğrulamalı HTTPS isteği. Kendinden imzalı kalmışsa burada patlar.
        steps.Add(await ProbeExternalAsync(hostName, cancellationToken));

        await SaveHostNameAsync(hostName, cancellationToken);

        var (_, certificateInfo) = await ProbeCertificateAsync(hostName, cancellationToken);
        report.CertificateSource = certificateInfo.Source;
        report.CertificateIsSelfSigned = certificateInfo.IsSelfSigned;
        report.CertificateDaysLeft = certificateInfo.DaysLeft;

        report.Steps = steps;
        report.OverallStatus = CombineStatus(steps);
        return report;
    }

    // --- Adım: DNS ---

    /// <summary>
    /// Alan adının bu sunucuya çözülüp çözülmediğini HALKA AÇIK çözümleyicilerden (1.1.1.1 / 8.8.8.8)
    /// sorar — sunucunun kendi çözümleyicisi önbellekten/hosts dosyasından yanıt verip gerçeği gizleyebilir.
    /// </summary>
    private async Task<(PanelAccessStepDto Step, bool Matches)> CheckDnsAsync(
        string hostName,
        string serverAddress,
        CancellationToken cancellationToken)
    {
        var localAddresses = CollectLocalAddresses(serverAddress);
        var (resolved, notes) = await ResolveHostAddressesAsync(hostName, cancellationToken);

        var detail = new StringBuilder()
            .Append("host=").Append(hostName)
            .Append("; resolved=").Append(resolved.Count == 0 ? "(none)" : string.Join(",", resolved))
            .Append("; server=").Append(localAddresses.Count == 0 ? "(unknown)" : string.Join(",", localAddresses));

        if (notes.Count > 0)
        {
            detail.Append("; notes=").Append(string.Join(" | ", notes.Take(4)));
        }

        if (resolved.Count == 0)
        {
            return (Step("dns", "fail", string.Empty, detail.ToString()), false);
        }

        var matches = resolved.Any(address => localAddresses.Contains(address, StringComparer.OrdinalIgnoreCase));
        if (matches)
        {
            return (Step("dns", "ok", string.Join(", ", resolved), detail.ToString()), true);
        }

        // Sunucu NAT arkasındaysa (yerel adres 10.x/192.168.x) dış IP bilinemez; bunu "eşleşmedi"
        // diye kesin hata saymak yanlış olurdu — uyarı verip devam ederiz, doğruyu 443 ölçümü söyler.
        if (localAddresses.All(IsPrivateAddress))
        {
            detail.Append("; note=server addresses are all private (NAT); comparison inconclusive");
            return (Step("dns", "warn", string.Join(", ", resolved), detail.ToString()), true);
        }

        return (Step("dns", "fail", string.Join(", ", resolved), detail.ToString()), false);
    }

    private async Task<(IReadOnlyList<string> Addresses, IReadOnlyList<string> Notes)> ResolveHostAddressesAsync(
        string hostName,
        CancellationToken cancellationToken)
    {
        // Alan adı doğrudan PowerShell'e gömüldüğü için NormalizeHostName katı doğrulama yapar
        // (komut enjeksiyonu yok). Değişken adı $panelHost: $host PowerShell'de ayrılmış otomatik değişkendir.
        var script = $"$panelHost = '{hostName}'\r\n{DnsProbeScript}";

        try
        {
            var run = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            var json = run.StandardOutput?.Trim() ?? string.Empty;
            var start = json.IndexOf('{');
            if (start >= 0)
            {
                var probe = JsonSerializer.Deserialize<DnsProbe>(
                    json[start..],
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (probe is not null && probe.Addresses.Length > 0)
                {
                    return (probe.Addresses, probe.Errors);
                }

                if (probe is not null)
                {
                    return (Array.Empty<string>(), probe.Errors);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Panel alan adı DNS ön kontrolü (PowerShell) çalıştırılamadı; yerel çözümleyiciye düşülüyor.");
        }

        // Halka açık çözümleyici sorgusu yapılamadıysa yolu KAPATMA: yerel çözümleyiciyle dene.
        try
        {
            // Tam nitelikli: bu derlemede AdaPanel.Infrastructure.Dns ad alanı da var, kısa ad çakışır.
            var addresses = await System.Net.Dns.GetHostAddressesAsync(hostName, cancellationToken);
            return (
                addresses.Select(address => address.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                new[] { "public resolver query unavailable; used local resolver" });
        }
        catch (Exception exception)
        {
            return (Array.Empty<string>(), new[] { $"local resolver: {exception.GetType().Name}: {exception.Message}" });
        }
    }

    private const string DnsProbeScript =
        """
        $ErrorActionPreference = 'SilentlyContinue'
        $out = [ordered]@{ addresses = @(); errors = @() }
        foreach ($server in @('1.1.1.1', '8.8.8.8')) {
            try {
                $answers = Resolve-DnsName -Name $panelHost -Type A -Server $server -DnsOnly -QuickTimeout -ErrorAction Stop
                foreach ($answer in @($answers | Where-Object { $_.IPAddress })) {
                    $ip = [string]$answer.IPAddress
                    if ($out.addresses -notcontains $ip) { $out.addresses += $ip }
                }
            }
            catch { $out.errors += "$server : $($_.Exception.Message)" }
        }
        $out | ConvertTo-Json -Depth 3 -Compress
        """;

    private sealed class DnsProbe
    {
        public string[] Addresses { get; init; } = [];

        public string[] Errors { get; init; } = [];
    }

    // --- Adım: IIS sitesi ---

    private async Task<(PanelAccessStepDto Step, SiteDto? Site)> CheckSiteAsync(string hostName, CancellationToken cancellationToken)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(ProxySiteName, cancellationToken);
            var hasHost = site.Bindings.Any(binding =>
                string.Equals((binding.HostName ?? string.Empty).Trim(), hostName, StringComparison.OrdinalIgnoreCase));
            var hasHttps = site.Bindings.Any(binding =>
                string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase));

            var detail = $"site={site.Name}; state={site.State}; bindings={DescribeBindings(site)}";
            if (!hasHost)
            {
                return (Step("site", "fail", site.Name, detail), site);
            }

            var running = string.Equals(site.State, "Started", StringComparison.OrdinalIgnoreCase);
            var status = running && hasHttps ? "ok" : "warn";
            return (Step("site", status, site.Name, detail), site);
        }
        catch (Exception exception)
        {
            return (Step("site", "fail", string.Empty, Describe(exception)), null);
        }
    }

    /// <summary>
    /// Ters vekil sitesini oluşturur ya da mevcut siteyi korur. Mevcut siteyi ASLA yeniden
    /// oluşturmaz: CreateSiteAsync aynı adlı siteyi silip kurar, bu da 443 bağlamasını ve bağlı
    /// sertifikayı yok ederdi (kurulum ikinci kez çalıştırılınca panel dışarıya kapanırdı).
    /// </summary>
    private async Task<(PanelAccessStepDto Step, SiteDto? Site)> EnsureSiteAsync(
        string hostName,
        string defaultSiteRoot,
        CancellationToken cancellationToken)
    {
        SiteDto? existing = null;
        try
        {
            existing = await iisSiteService.GetSiteAsync(ProxySiteName, cancellationToken);
        }
        catch (AppException)
        {
            existing = null;
        }
        catch (Exception exception)
        {
            return (Step("site", "fail", string.Empty, Describe(exception)), null);
        }

        // Site kökü ProgramData ALTINDA DEĞİL: panel veri kökü güvenlik sertleştirmesiyle
        // (SYSTEM/Administrators) kilitlenir, IIS uygulama havuzu oradan dosya sunamaz —
        // ACME doğrulama dosyası okunamazdı. Standart site kökü kullanılır.
        var physicalPath = Path.Combine(
            Environment.ExpandEnvironmentVariables(
                string.IsNullOrWhiteSpace(defaultSiteRoot) ? "C:\\inetpub\\wwwroot" : defaultSiteRoot),
            "adapanel-panel-access");

        try
        {
            if (existing is null)
            {
                Directory.CreateDirectory(physicalPath);
                var created = await iisSiteService.CreateSiteAsync(
                    new CreateSiteRequest
                    {
                        Name = ProxySiteName,
                        PhysicalPath = physicalPath,
                        HostName = hostName,
                        IpAddress = "*",
                        Port = 80,
                        Protocol = "http",
                        AutoStart = true,
                        RuntimeProfile = "static",
                        InstallRuntimeIfMissing = false,
                        // Panelin kendi vekil sitesi için veritabanı/mail/FTP kaynağı üretilmez.
                        ProvisionDefaultServices = false
                    },
                    cancellationToken);

                var fresh = await iisSiteService.GetSiteAsync(created.Name, cancellationToken);
                return (
                    Step("site", "ok", fresh.Name, $"created; path={fresh.PhysicalPath}; bindings={DescribeBindings(fresh)}"),
                    fresh);
            }

            var hasHost = existing.Bindings.Any(binding =>
                string.Equals((binding.HostName ?? string.Empty).Trim(), hostName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(binding.Protocol, "http", StringComparison.OrdinalIgnoreCase));

            if (!hasHost)
            {
                // Alan adı değişmiş: yeni HTTP bağlamasını ekle, eski panel bağlamalarını kaldır.
                await iisSiteService.AddBindingAsync(
                    existing.Name,
                    new SiteBindingRequest { Protocol = "http", HostName = hostName, IpAddress = "*", Port = 80 },
                    cancellationToken);

                foreach (var stale in existing.Bindings.Where(binding =>
                             !string.IsNullOrWhiteSpace(binding.HostName) &&
                             !string.Equals(binding.HostName.Trim(), hostName, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        await iisSiteService.RemoveBindingAsync(
                            existing.Name,
                            new RemoveSiteBindingRequest
                            {
                                Protocol = stale.Protocol,
                                HostName = stale.HostName,
                                IpAddress = stale.IpAddress,
                                Port = stale.Port
                            },
                            cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Eski panel bağlaması kaldırılamadı ({Binding}).", stale.BindingInformation);
                    }
                }
            }

            var updated = await iisSiteService.GetSiteAsync(existing.Name, cancellationToken);
            return (
                Step("site", "ok", updated.Name, $"reused; path={updated.PhysicalPath}; bindings={DescribeBindings(updated)}"),
                updated);
        }
        catch (Exception exception)
        {
            return (Step("site", "fail", ProxySiteName, Describe(exception)), existing);
        }
    }

    // --- Adım: sertifika ---

    /// <summary>
    /// Mevcut <see cref="ISslService"/> ile Let's Encrypt sertifikası alır. DİKKAT: bu servis
    /// başarısızlıkta SESSİZCE kendinden imzalı sertifikaya düşer — panelin dışarıya açılması için
    /// bu bir BAŞARISIZLIKTIR (yapay zeka istemcisi yine bağlanamaz), o yüzden dönen kaynak
    /// ("letsencrypt" değilse) açıkça hata olarak raporlanır.
    /// </summary>
    private async Task<PanelAccessStepDto> IssueCertificateAsync(
        string hostName,
        SetupPanelAccessRequest request,
        CancellationToken cancellationToken)
    {
        var contactEmail = request.ContactEmail?.Trim() ?? string.Empty;
        if (contactEmail.Length == 0)
        {
            var stored = await fileStore.ReadAsync(AcmeContactSettings.Path, new AcmeContactSettings(), cancellationToken);
            contactEmail = stored.Email?.Trim() ?? string.Empty;
        }

        if (contactEmail.Length == 0)
        {
            return Step("certificate", "fail", string.Empty, "No ACME contact e-mail: pass contactEmail, or issue a certificate once from the SSL screen so it is stored in config/acme-contact.json.");
        }

        try
        {
            var certificate = await sslService.IssueLetsEncryptAsync(
                new IssueLetsEncryptRequest
                {
                    SiteName = ProxySiteName,
                    HostName = hostName,
                    ContactEmail = contactEmail,
                    UseStaging = request.UseStaging,
                    Wildcard = false
                },
                cancellationToken);

            var detail = $"source={certificate.Source}; subject={certificate.Subject}; notAfter={certificate.NotAfterUtc:yyyy-MM-dd}";
            if (!string.Equals(certificate.Source, "letsencrypt", StringComparison.OrdinalIgnoreCase))
            {
                // Kendinden imzalıya düşüldü: kullanıcıya "kuruldu" DEME.
                return Step("certificate", "fail", certificate.Source, detail);
            }

            var daysLeft = DaysLeft(certificate.NotAfterUtc);
            return Step("certificate", daysLeft > 0 ? "ok" : "fail", $"{daysLeft}", detail);
        }
        catch (Exception exception)
        {
            return Step("certificate", "fail", string.Empty, Describe(exception));
        }
    }

    /// <summary>443'te FİİLEN sunulan sertifikayı TLS el sıkışmasıyla okur (SNI = panel alan adı).</summary>
    private async Task<(PanelAccessStepDto Step, CertificateInfo Info)> ProbeCertificateAsync(
        string hostName,
        CancellationToken cancellationToken)
    {
        var info = new CertificateInfo();
        var detail = string.Empty;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var client = new TcpClient();
            await client.ConnectAsync(hostName, 443, timeout.Token);

            X509Certificate2? served = null;
            var policyErrors = SslPolicyErrors.None;
            using var stream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                // Zinciri doğrulamak İÇİN değil, sertifikayı GÖRMEK için bağlanıyoruz: geçersiz
                // sertifikayı da okuyup sebebiyle raporlayabilmeliyiz.
                userCertificateValidationCallback: (_, certificate, _, errors) =>
                {
                    policyErrors = errors;
                    if (certificate is not null)
                    {
                        served = new X509Certificate2(certificate);
                    }

                    return true;
                });

            await stream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = hostName },
                timeout.Token);

            if (served is not null)
            {
                info.DaysLeft = DaysLeft(served.NotAfter.ToUniversalTime());
                info.IsSelfSigned = string.Equals(served.Subject, served.Issuer, StringComparison.OrdinalIgnoreCase);
                info.Source = info.IsSelfSigned ? "self-signed" : "letsencrypt";
                detail = $"{hostName}:443 → issuer={served.Issuer}; notAfter={served.NotAfter.ToUniversalTime():yyyy-MM-dd}"
                    + (policyErrors == SslPolicyErrors.None ? string.Empty : $"; tls={policyErrors}");
                served.Dispose();
            }
        }
        catch (Exception exception)
        {
            detail = $"{hostName}:443 → {Describe(exception)}";
        }

        if (info.Source == "none")
        {
            return (Step("certificate", "fail", string.Empty, detail), info);
        }

        // Kendinden imzalı = yapay zeka istemcileri için ÇALIŞMIYOR demektir; "warn" yumuşatması yapma.
        var status = info.IsSelfSigned || info.DaysLeft <= 0
            ? "fail"
            : info.DaysLeft <= 14 ? "warn" : "ok";

        return (Step("certificate", status, $"{info.DaysLeft}", detail), info);
    }

    private sealed class CertificateInfo
    {
        public string Source { get; set; } = "none";

        public bool IsSelfSigned { get; set; }

        public int DaysLeft { get; set; }
    }

    // --- Adım: ters vekil + yenileme muafiyeti ---

    private async Task<(PanelAccessStepDto Proxy, PanelAccessStepDto Renewal)> CheckProxyAsync(
        SiteDto? site,
        string backendUrl,
        CancellationToken cancellationToken)
    {
        if (site is null)
        {
            const string detail = "The reverse proxy site does not exist yet.";
            return (Step("proxy", "unknown", string.Empty, detail), Step("renewal", "unknown", string.Empty, detail));
        }

        try
        {
            var configurationDto = await reverseProxyService.GetSiteConfigurationAsync(site.Name, cancellationToken);
            var destinationMatches = string.Equals(
                configurationDto.DestinationUrl?.TrimEnd('/'),
                backendUrl.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);

            var detail = $"arr={configurationDto.IsArrEnabled}; proxy={configurationDto.IsProxyEnabled}; " +
                $"destination={configurationDto.DestinationUrl}; expected={backendUrl}";

            var proxyStatus = configurationDto is { IsArrEnabled: true, IsProxyEnabled: true } && destinationMatches
                ? "ok"
                : configurationDto.IsProxyEnabled ? "warn" : "fail";

            var hasAcmeRule = configurationDto.RewriteRules.Any(rule =>
                string.Equals(rule.Name, AcmeExclusionRuleName, StringComparison.OrdinalIgnoreCase));

            var renewalStep = Step(
                "renewal",
                hasAcmeRule ? "ok" : "warn",
                hasAcmeRule ? AcmeExclusionRuleName : string.Empty,
                $"acmeExclusionRule={hasAcmeRule}; rules={configurationDto.RewriteRules.Count}");

            return (Step("proxy", proxyStatus, configurationDto.DestinationUrl ?? string.Empty, detail), renewalStep);
        }
        catch (Exception exception)
        {
            var detail = Describe(exception);
            return (Step("proxy", "fail", string.Empty, detail), Step("renewal", "unknown", string.Empty, detail));
        }
    }

    private async Task<PanelAccessStepDto> EnableProxyAsync(string backendUrl, CancellationToken cancellationToken)
    {
        try
        {
            var result = await reverseProxyService.EnableProxyAsync(
                ProxySiteName,
                new EnableReverseProxyRequest
                {
                    DestinationUrl = backendUrl,
                    MountPath = "/",
                    // Panel bir SPA'dır; HTML bağlantı yeniden yazımı kök montajda zaten uygulanmaz
                    // ve panelin kendi mutlak yollarını bozabilir.
                    EnableHtmlLinkRewrite = false
                },
                cancellationToken);

            var detail = $"arr={result.IsArrEnabled}; proxy={result.IsProxyEnabled}; destination={result.DestinationUrl}; " +
                $"webConfig={result.WebConfigPath}";

            return Step(
                "proxy",
                result is { IsArrEnabled: true, IsProxyEnabled: true } ? "ok" : "warn",
                result.DestinationUrl ?? backendUrl,
                detail);
        }
        catch (Exception exception)
        {
            return Step("proxy", "fail", string.Empty, Describe(exception));
        }
    }

    /// <summary>
    /// Kök vekil kuralı her isteği panele iletir; 60 gün sonraki Let's Encrypt YENİLEMESİ de bu
    /// yüzden başarısız olurdu. Bu kural, ACME doğrulama yolunu kural zincirinden erken çıkarıp
    /// IIS'in statik dosya işleyicisine bırakır (kendi kendine yeniden yazım + stopProcessing).
    /// </summary>
    private async Task<PanelAccessStepDto> EnsureAcmeExclusionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await reverseProxyService.AddRewriteRuleAsync(
                ProxySiteName,
                new AddRewriteRuleRequest
                {
                    Name = AcmeExclusionRuleName,
                    MatchPattern = "^\\.well-known/acme-challenge/(.*)$",
                    RewriteUrl = ".well-known/acme-challenge/{R:1}",
                    StopProcessing = true,
                    AppendQueryString = false,
                    IgnoreCase = true
                },
                cancellationToken);

            return Step("renewal", "ok", AcmeExclusionRuleName, "acme-challenge requests are served by IIS, not proxied");
        }
        catch (Exception exception)
        {
            // Yenileme muafiyeti kurulamazsa panel BUGÜN çalışır, 60 gün sonra sertifika yenilenemez.
            // Bu yüzden "warn": kurulum başarısız değil ama sessiz de geçilmemeli.
            return Step("renewal", "warn", string.Empty, Describe(exception));
        }
    }

    // --- Adım: dış erişim ---

    /// <summary>
    /// Yapay zeka istemcisinin yapacağı isteğin AYNISI: zincir doğrulamalı HTTPS. Kendinden imzalı
    /// sertifika burada başarısız olur — özelliğin var oluş sebebi tam olarak bu kontroldür.
    /// </summary>
    private async Task<PanelAccessStepDto> ProbeExternalAsync(string hostName, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            // Varsayılan (adsız) istemci sertifika zincirini DOĞRULAR — burada istediğimiz tam olarak bu.
            var client = httpClientFactory.CreateClient();
            client.Timeout = ProbeTimeout;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{hostName}/");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var code = (int)response.StatusCode;
            var status = code is >= 200 and < 500 ? "ok" : "warn";
            return Step("external", status, $"HTTP {code}", $"https://{hostName}/ → {code} {response.ReasonPhrase}");
        }
        catch (Exception exception)
        {
            return Step("external", "fail", string.Empty, $"https://{hostName}/ → {Describe(exception)}");
        }
    }

    // --- Yardımcılar ---

    private async Task SaveHostNameAsync(string hostName, CancellationToken cancellationToken)
    {
        // Atomik oku-değiştir-yaz: tercihler dosyasındaki diğer alanlar korunur.
        var current = await panelPreferencesService.GetAsync(cancellationToken);
        await fileStore.UpdateAsync(
            PreferencesPath,
            current,
            existing =>
            {
                existing.PanelHostName = hostName;
                return existing;
            },
            cancellationToken);
    }

    /// <summary>
    /// Ters vekilin ileteceği YEREL panel adresi.
    /// <para>
    /// "localhost" bilinçli seçimdir: panelin kendinden imzalı sertifikası (bkz.
    /// scripts/initialize-panel-certificate.ps1) SAN olarak "localhost" içerir VE kurulumda
    /// LocalMachine\Root deposuna eklenir. Böylece ARR arka uca bağlanırken hem ad hem zincir
    /// doğrulaması geçer; 127.0.0.1 kullanılsaydı ad uyuşmazlığı 502 üretebilirdi.
    /// </para>
    /// </summary>
    private string ResolveBackendUrl()
    {
        var dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));
        var hasCertificate =
            File.Exists(Path.Combine(dataRoot, "certs", "panel.pfx")) &&
            File.Exists(Path.Combine(dataRoot, "certs", "panel.password"));

        var port = ResolveListenerPort(
            configuration["ASPNETCORE_URLS"] ?? configuration["urls"],
            runtimeOptions.Value.PreferredHttpsPort);

        return hasCertificate ? $"https://localhost:{port}" : $"http://localhost:{port}";
    }

    /// <summary>Program.cs'teki dinleyici portu çözümlemesinin aynısı (panel gerçekte hangi portta dinliyor).</summary>
    private static int ResolveListenerPort(string? configuredUrls, int fallbackPort)
    {
        if (string.IsNullOrWhiteSpace(configuredUrls))
        {
            return fallbackPort;
        }

        foreach (var segment in configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(segment, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        return fallbackPort;
    }

    /// <summary>
    /// Alan adı hem IIS bağlamasına hem PowerShell scriptine gider → katı doğrulama (enjeksiyon yok).
    /// Joker (*) kabul edilmez: panel tek bir ada yayınlanır.
    /// </summary>
    private static string NormalizeHostName(string hostName)
    {
        var normalized = (hostName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (!HostNamePattern().IsMatch(normalized))
        {
            throw new AppException("panelAccess.invalid_host_name", 400, 400);
        }

        return normalized;
    }

    private static IReadOnlyCollection<string> CollectLocalAddresses(string serverAddress)
    {
        var addresses = new List<string>();
        try
        {
            addresses.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => !IPAddress.IsLoopback(address) &&
                    !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(address => address.ToString()));
        }
        catch
        {
            // Ağ arayüzleri okunamazsa yalnız panelin bildiği sunucu adresiyle karşılaştırılır.
        }

        if (!string.IsNullOrWhiteSpace(serverAddress) && IPAddress.TryParse(serverAddress.Trim(), out _))
        {
            addresses.Add(serverAddress.Trim());
        }

        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsPrivateAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();
        return octets[0] == 10 ||
            (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) ||
            (octets[0] == 192 && octets[1] == 168) ||
            (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127);
    }

    private static string DescribeBindings(SiteDto site)
    {
        return site.Bindings.Count == 0
            ? "(none)"
            : string.Join(", ", site.Bindings.Select(binding => $"{binding.Protocol}:{binding.BindingInformation}"));
    }

    private static string Describe(Exception exception)
    {
        var builder = new StringBuilder($"{exception.GetType().Name}: {exception.Message}");
        if (exception is AppException appException && !string.IsNullOrWhiteSpace(appException.Details))
        {
            var details = appException.Details;
            builder.Append(" | ").Append(details.Length > 1500 ? details[^1500..] : details);
        }

        var inner = exception.InnerException;
        while (inner is not null)
        {
            builder.Append(" <- ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
            inner = inner.InnerException;
        }

        return builder.ToString();
    }

    private static int DaysLeft(DateTime notAfterUtc)
    {
        return (int)Math.Floor((notAfterUtc - DateTime.UtcNow).TotalDays);
    }

    private static PanelAccessStepDto Step(string key, string status, string value, string detail)
    {
        return new PanelAccessStepDto
        {
            Key = key,
            Status = status,
            Value = value,
            Detail = detail
        };
    }

    private static IReadOnlyList<PanelAccessStepDto> BuildUnknownSteps(string detail)
    {
        return StepOrder.Select(key => Step(key, "unknown", string.Empty, detail)).ToArray();
    }

    /// <summary>Erken çıkışta kalan adımlar "unknown" olarak DOLDURULUR: arayüzde satır kaybolmaz.</summary>
    private static IReadOnlyList<PanelAccessStepDto> Pad(List<PanelAccessStepDto> steps, string detail)
    {
        var known = steps.Select(step => step.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var padded = new List<PanelAccessStepDto>(steps);
        padded.AddRange(StepOrder
            .Where(key => !known.Contains(key))
            .Select(key => Step(key, "unknown", string.Empty, detail)));

        return padded
            .OrderBy(step => Array.IndexOf(StepOrder, step.Key))
            .ToArray();
    }

    private static readonly string[] StepOrder = ["dns", "site", "certificate", "renewal", "proxy", "external"];

    private static string CombineStatus(IEnumerable<PanelAccessStepDto> steps)
    {
        var statuses = steps.Select(step => step.Status).ToArray();
        if (statuses.Any(status => status == "fail"))
        {
            return "fail";
        }

        if (statuses.Any(status => status == "warn"))
        {
            return "warn";
        }

        return statuses.All(status => status == "ok") ? "ok" : "unknown";
    }

    [GeneratedRegex("^(?!-)[a-z0-9-]{1,63}(?<!-)(\\.(?!-)[a-z0-9-]{1,63}(?<!-))+$", RegexOptions.Compiled)]
    private static partial Regex HostNamePattern();
}
