using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Iis;

/// <inheritdoc />
public sealed class SiteAliasService(
    IIisSiteService iisSiteService,
    JsonFileStore fileStore,
    ILogger<SiteAliasService> logger) : ISiteAliasService
{
    private const string StatePath = "config/site-aliases.json";

    /// <summary>Yönlendirme kuralının web.config'teki adı — panelin kendi kuralını tanıyıp güncelleyebilmesi için.</summary>
    private const string RuleName = "AdaPanel.AliasRedirect";

    /// <inheritdoc />
    public async Task<SiteAliasWorkspaceDto> GetAliasesAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var kayitlar = await KayitlariOkuAsync(cancellationToken);
        var siteKayitlari = kayitlar.Where(item => Esit(item.SiteName, siteName)).ToArray();

        var httpsHostlari = site.Bindings
            .Where(binding => string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase))
            .Select(binding => Normalle(binding.HostName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var baglamalar = site.Bindings.Select(binding => Normalle(binding.HostName)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new SiteAliasWorkspaceDto
        {
            SiteName = site.Name,
            PrimaryHostName = AnaHost(site),
            Aliases = siteKayitlari
                // Kayıtta olup IIS'te OLMAYAN takma ad gösterilmez: panel, gerçekte olmayan bir
                // şeyi varmış gibi listelememeli (bağlama elle silinmiş olabilir).
                .Where(kayit => baglamalar.Contains(kayit.HostName))
                .Select(kayit => new SiteAliasDto
                {
                    HostName = kayit.HostName,
                    RedirectToPrimary = kayit.RedirectToPrimary,
                    HasHttps = httpsHostlari.Contains(kayit.HostName),
                    IncludesWww = baglamalar.Contains("www." + kayit.HostName)
                })
                .OrderBy(item => item.HostName, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    /// <inheritdoc />
    public async Task<SiteAliasWorkspaceDto> AddAliasAsync(string siteName, AddSiteAliasRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var host = Normalle(request.HostName);
        if (host.Length == 0)
        {
            throw new AppException("alias.host_required", 400, 400);
        }

        var ana = AnaHost(site);
        if (Esit(host, ana))
        {
            // Sitenin KENDİ adını takma ad yapmak, yönlendirme açıksa siteyi kendine sonsuz
            // yönlendirirdi. Sessizce kabul etmek yerine sebebi söylüyoruz.
            throw new AppException("alias.same_as_primary", 400, 400);
        }

        // www çifti: kullanıcı "ornek.com.tr" yazınca "www.ornek.com.tr" de beklenir. İkisini ayrı
        // ayrı eklettirmek, birini unutmanın ve sitenin www'de açılmamasının yolu.
        var hostlar = new List<string> { host };
        if (request.IncludeWww && !host.StartsWith("www.", StringComparison.Ordinal))
        {
            hostlar.Add("www." + host);
        }

        foreach (var eklenecek in hostlar)
        {
            if (site.Bindings.Any(binding => Esit(Normalle(binding.HostName), eklenecek)))
            {
                continue; // zaten bağlı; idempotent
            }

            await iisSiteService.AddBindingAsync(
                siteName,
                new SiteBindingRequest { Protocol = "http", HostName = eklenecek, IpAddress = "*", Port = 80 },
                cancellationToken);
        }

        await KayitYazAsync(siteName, host, request.RedirectToPrimary, sil: false, cancellationToken);
        YonlendirmeKuraliniYaz(site, await KayitlariOkuAsync(cancellationToken), ana);

        return await GetAliasesAsync(siteName, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SiteAliasWorkspaceDto> RemoveAliasAsync(string siteName, string hostName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var host = Normalle(hostName);

        // BAĞLAMALARI DA KALDIR. Yalnız kaydı silmek, alan adını IIS'te bağlı bırakırdı: site o
        // adresten yayınlanmaya devam eder ama panelde görünmez — sonradan başka bir siteye
        // bağlanmak istendiğinde de "bu ad kullanımda" hatası verirdi.
        foreach (var protokol in new[] { "http", "https" })
        {
            foreach (var aday in new[] { host, "www." + host })
            {
                var baglama = site.Bindings.FirstOrDefault(item =>
                    Esit(Normalle(item.HostName), aday) &&
                    string.Equals(item.Protocol, protokol, StringComparison.OrdinalIgnoreCase));
                if (baglama is null)
                {
                    continue;
                }

                try
                {
                    await iisSiteService.RemoveBindingAsync(
                        siteName,
                        new RemoveSiteBindingRequest
                        {
                            Protocol = baglama.Protocol,
                            HostName = baglama.HostName,
                            IpAddress = baglama.IpAddress,
                            Port = baglama.Port
                        },
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    // Bir bağlamanın kaldırılamaması kaydın silinmesini engellemez; kullanıcı
                    // sonucu görsün diye günlüğe yazılır.
                    logger.LogWarning(exception, "Takma ad bağlaması kaldırılamadı: {Site} → {Host}", siteName, aday);
                }
            }
        }

        await KayitYazAsync(siteName, host, redirect: false, sil: true, cancellationToken);

        var taze = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        YonlendirmeKuraliniYaz(taze, await KayitlariOkuAsync(cancellationToken), AnaHost(taze));

        return await GetAliasesAsync(siteName, cancellationToken);
    }

    /// <summary>
    /// Yönlendirmesi açık takma adları TEK bir URL Rewrite kuralına yazar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kural adı sabittir; panel her seferinde kendi kuralını bulup değiştirir, kullanıcının
    /// kendi kurallarına dokunmaz. Yönlendirilecek ad kalmadıysa kural tamamen kaldırılır —
    /// boş bir kural bırakmak, sonradan "neden çalışmıyor" sorusunu doğururdu.
    /// </para>
    /// <para>
    /// HTTPS bilgisi korunur (<c>{HTTPS}</c> koşulu değil, <c>{MapProtocol:}</c> yerine basit ve
    /// güvenli yol: mevcut şemayı sunucu değişkeninden okumak yerine her iki şema için de tek
    /// hedef kullanılır) — ziyaretçi güvenli bağlantıdan geldiyse güvenli kalır.
    /// </para>
    /// </remarks>
    private void YonlendirmeKuraliniYaz(SiteDto site, IReadOnlyCollection<AliasKaydi> kayitlar, string anaHost)
    {
        var kok = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        if (string.IsNullOrWhiteSpace(kok) || !Directory.Exists(kok))
        {
            return;
        }

        var yonlendirilecek = kayitlar
            .Where(item => Esit(item.SiteName, site.Name) && item.RedirectToPrimary)
            .SelectMany(item => new[] { item.HostName, "www." + item.HostName })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var webConfig = Path.Combine(kok, "web.config");

        try
        {
            var belge = File.Exists(webConfig)
                ? XDocument.Load(webConfig, LoadOptions.PreserveWhitespace)
                : new XDocument(new XElement("configuration"));

            var configuration = belge.Element("configuration")!;
            var systemWebServer = Bul(configuration, "system.webServer");
            var rewrite = systemWebServer.Element("rewrite");
            var rules = rewrite?.Element("rules");

            // Önce kendi kuralımızı temizle (idempotent).
            rules?.Elements("rule").Where(item => (string?)item.Attribute("name") == RuleName).Remove();

            if (yonlendirilecek.Length > 0 && anaHost.Length > 0)
            {
                rewrite ??= Bul(systemWebServer, "rewrite");
                rules ??= Bul(rewrite, "rules");

                var desen = string.Join("|", yonlendirilecek.Select(Regex.Escape));
                var kural = new XElement(
                    "rule",
                    new XAttribute("name", RuleName),
                    new XAttribute("stopProcessing", "true"),
                    new XElement("match", new XAttribute("url", "(.*)")),
                    new XElement(
                        "conditions",
                        new XElement(
                            "add",
                            new XAttribute("input", "{HTTP_HOST}"),
                            new XAttribute("pattern", $"^({desen})$"))),
                    new XElement(
                        "action",
                        new XAttribute("type", "Redirect"),
                        new XAttribute("url", $"https://{anaHost}/{{R:1}}"),
                        new XAttribute("redirectType", "Permanent")));

                // Kural EN BAŞA: kullanıcının kendi kuralları (ör. WordPress permalink) isteği
                // yakalayıp yönlendirmeyi hiç çalıştırmayabilirdi.
                rules.AddFirst(kural);
            }

            belge.Save(webConfig);
        }
        catch (Exception exception)
        {
            // Yönlendirme kuralı yazılamazsa takma ad YİNE ÇALIŞIR (bağlama duruyor); yalnız
            // kanonik yönlendirme olmaz. Sessiz geçmiyoruz.
            logger.LogWarning(exception, "Takma ad yönlendirme kuralı yazılamadı: {Site}", site.Name);
        }
    }

    private static XElement Bul(XElement parent, string name)
    {
        var element = parent.Element(name);
        if (element is null)
        {
            element = new XElement(name);
            parent.Add(element);
        }

        return element;
    }

    /// <summary>Sitenin ana alan adı: takma ad OLMAYAN ilk gerçek host.</summary>
    private static string AnaHost(SiteDto site)
        => site.Bindings
            .Select(binding => Normalle(binding.HostName))
            .FirstOrDefault(host => host.Length > 0 && !host.StartsWith("www.", StringComparison.Ordinal) && !host.EndsWith(".nip.io", StringComparison.OrdinalIgnoreCase))
           ?? string.Empty;

    private async Task<IReadOnlyCollection<AliasKaydi>> KayitlariOkuAsync(CancellationToken cancellationToken)
        => await fileStore.ReadAsync(StatePath, Array.Empty<AliasKaydi>(), cancellationToken);

    private Task KayitYazAsync(string siteName, string host, bool redirect, bool sil, CancellationToken cancellationToken)
        => fileStore.UpdateAsync(StatePath, Array.Empty<AliasKaydi>(), mevcut =>
        {
            var liste = mevcut
                .Where(item => !(Esit(item.SiteName, siteName) && Esit(item.HostName, host)))
                .ToList();

            if (!sil)
            {
                liste.Add(new AliasKaydi { SiteName = siteName, HostName = host, RedirectToPrimary = redirect });
            }

            return liste.ToArray();
        }, cancellationToken);

    private static string Normalle(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static bool Esit(string? a, string? b)
        => string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed class AliasKaydi
    {
        public string SiteName { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public bool RedirectToPrimary { get; set; }
    }
}
