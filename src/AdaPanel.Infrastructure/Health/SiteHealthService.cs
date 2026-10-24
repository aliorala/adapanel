using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Health;

/// <summary>
/// Sitenin DIŞARIDAN görünen sağlığı.
/// <para>
/// 2026-07-23 olayı bu servisin varlık sebebidir: DNS servisi "Running", site "Started", sertifika
/// "geçerli" görünürken alan adları dışarıdan altı gün boyunca çözülemedi. Panelin kendi iç durum
/// sinyalleri bunu ASLA yakalayamaz — bu yüzden burada yalnız dış gözlem yapılır: halka açık
/// çözümleyiciden DNS, gerçek HTTP isteği, 443'te sunulan gerçek sertifika, canlı mail kayıtları.
/// </para>
/// Her kontrol ayrı try/catch içindedir (kalıp: <see cref="Advisor.AdvisorService"/>): biri patlasa
/// diğerleri raporlanmaya devam eder, kullanıcı hiçbir zaman boş ekran görmez.
/// </summary>
public sealed partial class SiteHealthService(
    IIisSiteService iisSiteService,
    IPanelPreferencesService panelPreferencesService,
    IMailService mailService,
    JsonFileStore fileStore,
    IHttpClientFactory httpClientFactory) : ISiteHealthService
{
    private const string SslStatePath = "config/ssl.json";

    // Her dış çağrıya kısa timeout: sağlık ölçümü bir kullanıcı isteğini asla dakikalarca dondurmamalı.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    // DKIM seçicisi (selector) alan adına göre değişir; mail sunucusundan öğrenilemezse yaygın
    // seçiciler denenir. Yanlış "DKIM yok" demektense birkaç ucuz sorgu daha atmayı tercih ediyoruz.
    private static readonly string[] FallbackDkimSelectors = ["default", "mail", "dkim", "stalwart"];

    public async Task<SiteHealthDto> GetSiteHealthAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var domain = ResolveDomain(site);
        var zones = await fileStore.ReadAsync("config/dns-zones.json", Array.Empty<HealthZone>(), cancellationToken);
        var configuredMailDomain = zones.FirstOrDefault(zone => string.Equals(zone.SiteName, site.Name, StringComparison.OrdinalIgnoreCase))?.ZoneName?.Trim().TrimEnd('.').ToLowerInvariant();
        var mailDomain = !string.IsNullOrWhiteSpace(configuredMailDomain) && SiteDomainSelection.IsRealDomain(configuredMailDomain) && IsSafeDnsName(configuredMailDomain) &&
            (domain == configuredMailDomain || domain.EndsWith("." + configuredMailDomain, StringComparison.OrdinalIgnoreCase))
            ? configuredMailDomain : domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? domain[4..] : domain;

        var serverAddress = string.Empty;
        try
        {
            serverAddress = (await panelPreferencesService.GetAsync(cancellationToken)).ServerAddress?.Trim() ?? string.Empty;
            var dnsTemplate = await fileStore.ReadAsync("config/dns-template.json", new DnsTemplateDto(), cancellationToken);
            var mapping = dnsTemplate.IpMappings.FirstOrDefault(item => site.Bindings.Any(binding => binding.IpAddress == item.PrivateAddress));
            if (mapping is not null) serverAddress = mapping.PublicAddress;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* tercihler okunamazsa sunucu adresi karşılaştırması "unknown" kalır */ }

        var report = new SiteHealthDto
        {
            SiteName = site.Name,
            Domain = domain,
            MailDomain = mailDomain,
            ServerAddress = serverAddress,
            CheckedAtUtc = DateTime.UtcNow
        };

        if (string.IsNullOrEmpty(domain))
        {
            // Alan adı yoksa dışarıdan ölçülecek bir şey de yok; "ok" demek yanıltıcı olurdu.
            const string detail = "Sitenin IIS binding'lerinde alan adı yok.";
            report.DnsResolves = Check("dnsResolves", "unknown", string.Empty, detail);
            report.DnsMatchesServer = Check("dnsMatchesServer", "unknown", string.Empty, detail);
            report.Http = Check("http", "unknown", string.Empty, detail);
            report.Https = Check("https", "unknown", string.Empty, detail);
            report.Mail = Check("mail", "unknown", string.Empty, detail);
            report.OverallStatus = "unknown";
            report.DurationMs = stopwatch.ElapsedMilliseconds;
            return report;
        }

        // Tek PowerShell turunda TÜM DNS sorguları (A x2 çözümleyici + MX + SPF + DMARC + DKIM):
        // her sorgu için ayrı süreç açmak ölçümü saniyelerce uzatırdı.
        var dkimHosts = await ResolveDkimHostsAsync(mailDomain, cancellationToken);
        var probe = await RunDnsProbeAsync(domain, mailDomain, dkimHosts, cancellationToken);

        var resolvedAddresses = Array.Empty<string>();
        if (probe is null)
        {
            report.DnsResolves = Check("dnsResolves", "unknown", string.Empty, "DNS sorgusu çalıştırılamadı (Resolve-DnsName).");
        }
        else
        {
            var (status, value, addresses, detail) = EvaluateResolvers(probe.Resolvers ?? []);
            resolvedAddresses = addresses;
            report.DnsResolves = Check("dnsResolves", status, value, detail);
        }

        var (matchStatus, matchValue, matchDetail) = EvaluateServerMatch(resolvedAddresses, serverAddress);
        report.DnsMatchesServer = Check("dnsMatchesServer", matchStatus, matchValue, matchDetail);

        report.Http = await ProbeHttpAsync(domain, serverAddress, cancellationToken);

        var (httpsCheck, daysLeft) = await ProbeHttpsAsync(site.Name, domain, serverAddress, cancellationToken);
        report.Https = httpsCheck;
        report.CertificateDaysLeft = daysLeft;

        var mailRecords = BuildMailChecks(probe);
        report.MailRecords = mailRecords;
        report.Mail = BuildMailSummary(mailRecords);

        report.OverallStatus = CombineStatus(
        [
            report.DnsResolves.Status,
            report.DnsMatchesServer.Status,
            report.Http.Status,
            report.Https.Status,
            report.Mail.Status
        ]);

        report.DurationMs = stopwatch.ElapsedMilliseconds;
        return report;
    }

    // --- HTTP ---

    /// <summary>
    /// DNS'e GÜVENMEDEN sitenin sunulup sunulmadığını ölçer: istek sunucunun KENDİ adresine atılır,
    /// hangi siteye gideceğini <c>Host</c> başlığı söyler. Böylece alan adı hiç çözülmese bile
    /// "IIS bu siteyi doğru döndürüyor mu" ayrı ayrı görülür (canlı doğrulandı: Invoke-WebRequest
    /// Host başlığını reddeder, HttpClient kabul eder).
    /// </summary>
    internal async Task<SiteHealthCheckDto> ProbeHttpAsync(string domain, string serverAddress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ResolveProbeTarget(serverAddress);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{target}/");
            request.Headers.Host = domain;

            // "AdaPanel.LocalHealth" istemcisi yönlendirmeyi İZLEMEZ: 301/302 zaten "site ayakta"
            // demektir, hedefi izlemek gereksiz gecikme ve yanlış negatif üretir.
            using var response = await httpClientFactory
                .CreateClient("AdaPanel.LocalHealth")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var code = (int)response.StatusCode;
            return Check("http", EvaluateHttpStatus(code), $"HTTP {code}", $"{target} (Host: {domain}) → {code} {response.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return Check("http", "fail", "yanıt yok", $"{target} (Host: {domain}): {exception.GetType().Name}: {exception.Message}");
        }
    }

    // --- HTTPS / sertifika ---

    /// <summary>
    /// 443'te FİİLEN sunulan sertifikayı TLS el sıkışmasıyla okur (SNI = alan adı).
    /// Sertifika alınamadıysa panelin kendi <c>config/ssl.json</c> kaydına düşülür. Canlı sunulan
    /// geçersiz sertifika kayıtlı sertifikayla gizlenmez — yeni bir ACME çağrısı
    /// YAPILMAZ, yalnız mevcut durum okunur.
    /// </summary>
    internal async Task<(SiteHealthCheckDto Check, int? DaysLeft)> ProbeHttpsAsync(
        string siteName,
        string domain,
        string serverAddress,
        CancellationToken cancellationToken,
        int port = 443)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ResolveProbeTarget(serverAddress, forceBrackets: false);
        var nameMismatch = false;
        var policyErrors = SslPolicyErrors.None;
        var certificateObserved = false;
        int? daysLeft = null;
        var live = false;
        var detail = string.Empty;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var client = new TcpClient();
            await client.ConnectAsync(target, port, timeout.Token);

            using var stream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                // Record rejected certificates for diagnostics without accepting them.
                // Snapshot and dispose inside the callback: failed handshakes must not leak a certificate.
                userCertificateValidationCallback: (_, certificate, _, errors) =>
                {
                    policyErrors = errors;
                    if (certificate is not null)
                    {
                        using var served = new X509Certificate2(certificate);
                        certificateObserved = true;
                        nameMismatch = errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
                        var now = DateTime.UtcNow;
                        daysLeft = DaysLeft(served.NotAfter.ToUniversalTime(), now);
                        if (served.NotBefore.ToUniversalTime() > now || served.NotAfter.ToUniversalTime() <= now)
                            policyErrors |= SslPolicyErrors.RemoteCertificateChainErrors;
                        detail = $"{target}:{port} (SNI: {domain}) → {served.Subject}; bitiş {served.NotAfter.ToUniversalTime():yyyy-MM-dd}"
                            + (policyErrors == SslPolicyErrors.None ? string.Empty : $"; tls={policyErrors}");
                    }

                    return certificate is not null && policyErrors == SslPolicyErrors.None;
                });

            await stream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = domain,
                    CertificateRevocationCheckMode = X509RevocationMode.Online
                },
                timeout.Token);

            live = certificateObserved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            detail = (detail + $" {target}:{port} (SNI: {domain}): {exception.GetType().Name}: {exception.Message}").Trim();
        }

        if (!certificateObserved)
        {
            // Canlı sertifika alınamadı → panelin kayıtlı durumuna düş (ACME çağrısı YOK).
            try
            {
                var certificates = await fileStore.ReadAsync(SslStatePath, Array.Empty<SslCertificateDto>(), cancellationToken);
                var stored = FindStoredCertificate(certificates, siteName, domain);
                if (stored is not null)
                {
                    daysLeft = DaysLeft(stored.NotAfterUtc, DateTime.UtcNow);
                    // "Ölçülemedi" ile "var/yok" AYRI yazılır. Bu alan bir zamanlar hiç ölçülmeden
                    // sabit true yazılıyordu ve bu satır, 443 el sıkışması başarısızken bile
                    // "https binding=True" diyerek kullanıcıyı yanlış yöne gönderiyordu.
                    var bindingDurumu = stored.IsHttpsBindingPresent switch
                    {
                        true => "var",
                        false => "yok",
                        null => "ölçülemedi"
                    };
                    detail = $"{port}'te sertifika alınamadı; panel kaydı: {stored.HostName} ({stored.NotAfterUtc:yyyy-MM-dd}), https bağlaması: {bindingDurumu}. {detail}".Trim();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* Stored state cannot establish a successful live TLS connection. */ }
        }

        var status = EvaluateCertificateStatus(daysLeft, nameMismatch, live, policyErrors);
        var value = daysLeft is null ? "yok" : $"{daysLeft} gün";
        return (Check("https", status, value, detail), daysLeft);
    }

    /// <summary>ssl.json içinde bu siteye/alan adına ait kaydı bulur (host eşleşmesi önce, sonra site adı).</summary>
    internal static SslCertificateDto? FindStoredCertificate(IEnumerable<SslCertificateDto> certificates, string siteName, string domain)
    {
        var list = certificates.ToArray();
        return list.FirstOrDefault(item => string.Equals(item.HostName?.Trim(), domain, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(item => string.Equals(item.SiteName?.Trim(), siteName, StringComparison.OrdinalIgnoreCase));
    }

    // --- DNS ---

    /// <summary>
    /// DKIM seçicisi alan adına özgüdür. Mail servisinin ürettiği kayıtlardan gerçek seçiciyi almaya
    /// çalışır (en doğru kaynak); mail sunucusu kurulu değilse/patlarsa yaygın seçicilere düşer.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveDkimHostsAsync(string domain, CancellationToken cancellationToken)
    {
        var hosts = new List<string>();
        try
        {
            var dns = await mailService.GetDnsRecordsAsync(domain, cancellationToken);
            foreach (var record in dns.Records ?? [])
            {
                if (!string.Equals(record.Purpose, "dkim", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var host = (record.Host ?? string.Empty).Trim().TrimEnd('.');
                if (host.Length == 0)
                {
                    continue;
                }

                hosts.Add(host.EndsWith(domain, StringComparison.OrdinalIgnoreCase) ? host : $"{host}.{domain}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* mail sunucusu yok/yanıt vermiyor → yaygın seçicilerle devam */ }

        foreach (var selector in FallbackDkimSelectors)
        {
            hosts.Add($"{selector}._domainkey.{domain}");
        }

        return hosts
            .Where(IsSafeDnsName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static async Task<DnsProbe?> RunDnsProbeAsync(string domain, string mailDomain, IReadOnlyList<string> dkimHosts, CancellationToken cancellationToken)
    {
        try
        {
            var script = new StringBuilder();
            script.Append("$domain = '").Append(domain).AppendLine("'");
            script.Append("$mailDomain = '").Append(mailDomain).AppendLine("'");
            script.Append("$dkimHosts = @(")
                .Append(string.Join(",", dkimHosts.Select(host => $"'{host}'")))
                .AppendLine(")");
            script.AppendLine(DnsProbeScript);

            var run = await ProcessRunner.RunPowerShellAsync(script.ToString(), cancellationToken);
            return ParseDnsProbe(run.StandardOutput);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
    }

    // Halka açık çözümleyicilere (1.1.1.1 / 8.8.8.8) SORULUR: sunucunun kendi çözümleyicisi
    // önbellekten/hosts dosyasından yanıt verip gerçeği gizleyebilir. -DnsOnly NetBIOS/LLMNR
    // yedeklerini kapatır, -QuickTimeout takılan çözümleyicide ölçümü kilitlemez.
    private const string DnsProbeScript =
        """
        $ErrorActionPreference = 'SilentlyContinue'
        $out = [ordered]@{
            resolvers = @()
            mxRecords = @()
            spf = ''
            dkim = ''
            dmarc = ''
            notes = @()
        }

        function Invoke-Probe {
            param([string]$Name, [string]$Type, [string]$Server)
            try {
                return @(Resolve-DnsName -Name $Name -Type $Type -Server $Server -DnsOnly -QuickTimeout -ErrorAction Stop)
            }
            catch {
                return @()
            }
        }

        foreach ($server in @('1.1.1.1', '8.8.8.8')) {
            $entry = [ordered]@{ server = $server; addresses = @(); error = '' }
            try {
                $answers = Resolve-DnsName -Name $domain -Type A -Server $server -DnsOnly -QuickTimeout -ErrorAction Stop
                # IPAddress alanı yalnız A/AAAA kayıtlarında dolu; CNAME zinciri böyle elenir.
                $entry.addresses = @($answers | Where-Object { $_.IPAddress } | ForEach-Object { [string]$_.IPAddress } | Sort-Object -Unique)
            }
            catch {
                $entry.error = [string]$_.Exception.Message
            }
            $out.resolvers += , $entry
        }

        # Mail kayıtları için tek çözümleyici yeter: soru "kayıt VAR MI", "her yerde aynı mı" değil.
        $out.mxRecords = @(Invoke-Probe $mailDomain 'MX' '1.1.1.1' | Where-Object { $_.NameExchange } | ForEach-Object { [string]$_.NameExchange })

        $txt = @(Invoke-Probe $mailDomain 'TXT' '1.1.1.1' | Where-Object { $_.Strings } | ForEach-Object { [string]($_.Strings -join '') })
        $spf = $txt | Where-Object { $_ -like 'v=spf1*' } | Select-Object -First 1
        if ($spf) { $out.spf = [string]$spf }

        $dmarcTxt = @(Invoke-Probe ('_dmarc.' + $mailDomain) 'TXT' '1.1.1.1' | Where-Object { $_.Strings } | ForEach-Object { [string]($_.Strings -join '') })
        $dmarc = $dmarcTxt | Where-Object { $_ -like '*v=DMARC1*' } | Select-Object -First 1
        if ($dmarc) { $out.dmarc = [string]$dmarc }

        foreach ($dkimName in $dkimHosts) {
            if ($out.dkim -ne '') { break }
            $values = @(Invoke-Probe $dkimName 'TXT' '1.1.1.1' | Where-Object { $_.Strings } | ForEach-Object { [string]($_.Strings -join '') })
            $found = $values | Where-Object { $_ -like '*v=DKIM1*' } | Select-Object -First 1
            if ($found) { $out.dkim = [string]$found }
        }

        $out | ConvertTo-Json -Depth 4 -Compress
        """;

    // --- Saf mantık (test edilebilir) ---

    /// <summary>Sitenin ölçülecek alan adı: binding'lerdeki ilk gerçek host adı, yoksa nokta içeren site adı.</summary>
    internal static string ResolveDomain(SiteDto site)
    {
        var hostName = SiteDomainSelection.WebDomain(site);

        if (!string.IsNullOrEmpty(hostName) && IsSafeDnsName(hostName))
        {
            return hostName;
        }

        var fromName = (site.Name ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        return SiteDomainSelection.IsRealDomain(fromName) && IsSafeDnsName(fromName) ? fromName : string.Empty;
    }

    private sealed class HealthZone
    {
        public string SiteName { get; set; } = "";
        public string ZoneName { get; set; } = "";
    }

    /// <summary>
    /// PowerShell'e tek tırnak içinde gömülecek adların KATI doğrulaması. Enjeksiyon riskini
    /// kaçış karakterine değil, "beyaz liste dışında hiçbir şey geçmez" kuralına dayandırıyoruz.
    /// </summary>
    internal static bool IsSafeDnsName(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 253 && SafeDnsNamePattern().IsMatch(value);

    /// <summary>
    /// İki halka açık çözümleyicinin sonucunu tek duruma indirir. İkisi de çözemiyorsa alan adı
    /// dünyaya kapalıdır (fail); yalnız biri çözüyorsa yayılma/çözümleyici sorunu vardır (warn).
    /// </summary>
    internal static (string Status, string Value, string[] Addresses, string Detail) EvaluateResolvers(IReadOnlyList<DnsResolverProbe> resolvers)
    {
        if (resolvers.Count == 0)
        {
            return ("unknown", string.Empty, [], "Hiçbir çözümleyici sorgulanamadı.");
        }

        var answered = resolvers.Where(resolver => (resolver.Addresses ?? []).Length > 0).ToArray();
        var addresses = resolvers
            .SelectMany(resolver => resolver.Addresses ?? [])
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var detail = string.Join("; ", resolvers.Select(resolver =>
        {
            var found = (resolver.Addresses ?? []).Length > 0
                ? string.Join(",", resolver.Addresses!)
                : string.IsNullOrWhiteSpace(resolver.Error) ? "(kayıt yok)" : resolver.Error!.Trim();
            return $"{resolver.Server}: {found}";
        }));

        var status = answered.Length switch
        {
            0 => "fail",
            var count when count == resolvers.Count => "ok",
            _ => "warn"
        };

        return (status, addresses.Length > 0 ? string.Join(", ", addresses) : "çözülmüyor", addresses, detail);
    }

    /// <summary>
    /// Çözülen IP bu sunucuyu mu gösteriyor. Panelde sunucu adresi yoksa ya da alan adı hiç
    /// çözülmüyorsa KARŞILAŞTIRILAMAZ → "unknown" (yanlış yeşil vermemek için "ok" DEĞİL).
    /// </summary>
    internal static (string Status, string Value, string Detail) EvaluateServerMatch(IReadOnlyCollection<string> resolvedAddresses, string serverAddress)
    {
        if (string.IsNullOrWhiteSpace(serverAddress) || !IPAddress.TryParse(serverAddress.Trim(), out _))
        {
            return ("unknown", string.Empty, "Panel ayarlarında sunucu IP adresi tanımlı değil (Ayarlar → Sunucu adresi).");
        }

        if (resolvedAddresses.Count == 0)
        {
            return ("unknown", string.Empty, "Alan adı çözülmediği için karşılaştırma yapılamadı.");
        }

        var expected = serverAddress.Trim();
        var match = resolvedAddresses.Any(address => string.Equals(address.Trim(), expected, StringComparison.OrdinalIgnoreCase));
        return match
            ? ("ok", expected, $"Çözülen: {string.Join(", ", resolvedAddresses)}")
            : ("fail", string.Join(", ", resolvedAddresses), $"Beklenen {expected}, çözülen {string.Join(", ", resolvedAddresses)} — alan adı başka bir sunucuyu gösteriyor.");
    }

    /// <summary>
    /// HTTP durum kodunu sağlığa çevirir. 3xx "ayakta" sayılır (yönlendirme normaldir); 401/403
    /// site sunuluyor ama korumalı demektir → uyarı, hata değil.
    /// </summary>
    internal static string EvaluateHttpStatus(int statusCode) => statusCode switch
    {
        >= 200 and < 400 => "ok",
        >= 400 and < 500 => "warn",
        >= 500 => "fail",
        _ => "unknown"
    };

    /// <summary>
    /// Sertifika durumu. Alan adı eşleşmiyorsa tarayıcı zaten uyarı basar → "fail". Kayıt var ama
    /// 443'te sunulmuyorsa (live=false) sertifika fiilen kullanılmıyordur → "warn".
    /// </summary>
    internal static string EvaluateCertificateStatus(int? daysLeft, bool nameMismatch, bool live,
        SslPolicyErrors policyErrors = SslPolicyErrors.None)
    {
        if (daysLeft is null || policyErrors != SslPolicyErrors.None)
        {
            return "fail";
        }

        if (daysLeft < 0 || nameMismatch)
        {
            return "fail";
        }

        if (daysLeft <= 14 || !live)
        {
            return "warn";
        }

        return "ok";
    }

    /// <summary>Bitiş tarihinden kalan tam gün (geçmişse negatif).</summary>
    internal static int DaysLeft(DateTime notAfterUtc, DateTime nowUtc)
        => (int)Math.Floor((notAfterUtc - nowUtc).TotalDays);

    /// <summary>
    /// Mail kayıtlarının toplu durumu. Hiçbiri yoksa alan adında mail BARINDIRILMIYOR demektir →
    /// "unknown" (site sahibi mailini başka yerde tutuyor olabilir; kırmızı göstermek gürültü olur).
    /// </summary>
    internal static (string Status, string Value) EvaluateMail(bool mx, bool spf, bool dkim, bool dmarc)
    {
        var present = new[] { mx, spf, dkim, dmarc }.Count(found => found);
        if (present == 0)
        {
            return ("unknown", "yok");
        }

        var missing = new List<string>();
        if (!mx) { missing.Add("MX"); }
        if (!spf) { missing.Add("SPF"); }
        if (!dkim) { missing.Add("DKIM"); }
        if (!dmarc) { missing.Add("DMARC"); }

        return missing.Count == 0
            ? ("ok", "MX, SPF, DKIM, DMARC")
            : ("warn", $"eksik: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Kontrollerin birleşimi. "unknown" ASLA "ok"a terfi etmez ama tek başına da alarm üretmez:
    /// yalnız HER ŞEY ölçülemediyse rapor "unknown" olur.
    /// </summary>
    internal static string CombineStatus(IReadOnlyCollection<string> statuses)
    {
        if (statuses.Count == 0)
        {
            return "unknown";
        }

        if (statuses.Any(status => status == "fail"))
        {
            return "fail";
        }

        if (statuses.Any(status => status == "warn"))
        {
            return "warn";
        }

        return statuses.All(status => status == "unknown") ? "unknown" : "ok";
    }

    /// <summary>PowerShell'in ürettiği JSON'ı ayrıştırır (kalıp: DnsService.ParseProbe). Bozuksa null.</summary>
    internal static DnsProbe? ParseDnsProbe(string? standardOutput)
    {
        try
        {
            var text = (standardOutput ?? string.Empty).Trim();
            var start = text.IndexOf('{');
            if (start < 0)
            {
                return null;
            }

            return JsonSerializer.Deserialize<DnsProbe>(
                text[start..],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    // --- Yardımcılar ---

    internal static List<SiteHealthCheckDto> BuildMailChecks(DnsProbe? probe)
    {
        if (probe is null)
            return new[] { "mail.mx", "mail.spf", "mail.dkim", "mail.dmarc" }
                .Select(key => Check(key, "unknown", "ölçülemedi", "DNS sorgusu çalıştırılamadı (Resolve-DnsName)."))
                .ToList();
        var mx = probe?.MxRecords ?? [];
        var spf = probe?.Spf ?? string.Empty;
        var dkim = probe?.Dkim ?? string.Empty;
        var dmarc = probe?.Dmarc ?? string.Empty;

        return
        [
            RecordCheck("mail.mx", mx.Length > 0, mx.Length > 0 ? string.Join(", ", mx) : string.Empty),
            RecordCheck("mail.spf", spf.Length > 0, spf),
            RecordCheck("mail.dkim", dkim.Length > 0, dkim),
            RecordCheck("mail.dmarc", dmarc.Length > 0, dmarc)
        ];

        static SiteHealthCheckDto RecordCheck(string key, bool found, string value) => new()
        {
            Key = key,
            Status = found ? "ok" : "fail",
            Value = found ? "var" : "yok",
            Detail = value
        };
    }

    internal static SiteHealthCheckDto BuildMailSummary(IReadOnlyList<SiteHealthCheckDto> records)
    {
        if (records.Count == 0 || records.All(record => record.Status == "unknown"))
            return Check("mail", "unknown", "ölçülemedi", "Posta DNS kayıtları ölçülemedi.");
        bool Found(string key) => records.Any(record => record.Key == key && record.Status == "ok");

        var (status, value) = EvaluateMail(Found("mail.mx"), Found("mail.spf"), Found("mail.dkim"), Found("mail.dmarc"));
        var detail = string.Join("; ", records.Select(record => $"{record.Key[5..].ToUpperInvariant()}={record.Value}"));
        return Check("mail", status, value, detail);
    }

    private static SiteHealthCheckDto Check(string key, string status, string value, string detail)
        => new() { Key = key, Status = status, Value = value, Detail = detail };

    /// <summary>
    /// İsteğin gideceği adres: panelin bildiği sunucu IP'si. IP değilse (boş/host adı) loopback'e
    /// düşülür — amaç "IIS bu siteyi sunuyor mu", ad çözümlemesini tekrar denemek değil.
    /// </summary>
    private static string ResolveProbeTarget(string serverAddress, bool forceBrackets = true)
    {
        if (!IPAddress.TryParse((serverAddress ?? string.Empty).Trim(), out var address))
        {
            return "127.0.0.1";
        }

        return forceBrackets && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
    }

    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9_]([a-z0-9_-]*[a-z0-9_])?(\.[a-z0-9_]([a-z0-9_-]*[a-z0-9_])?)+$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeDnsNamePattern();

    /// <summary>Tek çözümleyicinin ham sonucu (PowerShell JSON'ından).</summary>
    internal sealed class DnsResolverProbe
    {
        public string Server { get; init; } = string.Empty;

        public string[]? Addresses { get; init; }

        public string? Error { get; init; }
    }

    /// <summary>
    /// Tek PowerShell turunun tüm DNS bulguları. Alanlar NULLABLE: PowerShell boş koleksiyonu
    /// bazen hiç yazmaz/null yazar; eksik alan ölçümün tamamını düşürmemeli.
    /// </summary>
    internal sealed class DnsProbe
    {
        public DnsResolverProbe[]? Resolvers { get; init; }

        public string[]? MxRecords { get; init; }

        public string? Spf { get; init; }

        public string? Dkim { get; init; }

        public string? Dmarc { get; init; }

        public string[]? Notes { get; init; }
    }
}
