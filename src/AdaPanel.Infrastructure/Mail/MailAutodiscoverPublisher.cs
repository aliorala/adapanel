using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Mail;

/// <inheritdoc cref="IMailAutodiscoverPublisher" />
public sealed partial class MailAutodiscoverPublisher(
    IIisSiteService iisSiteService,
    IReverseProxyService reverseProxyService,
    ISslService sslService,
    IDnsService dnsService,
    IMailService mailService,
    IConcurrencyLock concurrencyLock,
    IConfiguration configuration,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<MailAutodiscoverPublisher> logger) : IMailAutodiscoverPublisher
{
    /// <summary>Panelin eklediği hizmet ana adları. Geri alma tam olarak bunları kaldırır.</summary>
    private static readonly string[] HizmetOnekleri = ["autodiscover.", "autoconfig."];

    // --- Ölçüm ---

    /// <inheritdoc />
    public async Task<MailAutodiscoverPublicationDto> GetStatusAsync(string domain, CancellationToken cancellationToken = default)
    {
        var alan = NormalizeDomain(domain);
        var site = await FindSiteAsync(alan, cancellationToken);
        return await BuildReportAsync(alan, site, cancellationToken);
    }

    // --- Yayınlama ---

    /// <inheritdoc />
    public async Task<MailAutodiscoverPublicationDto> PublishAsync(
        string domain,
        string contactEmail,
        CancellationToken cancellationToken = default)
    {
        var alan = NormalizeDomain(domain);

        // Alan adı 80/443'ten hangi siteden servis ediliyor? Bulunamazsa yazacak bir yer YOKTUR.
        // Rastgele bir siteye yazmak, başka bir müşterinin web.config'ini değiştirmek olurdu.
        // MEVCUT mesaj anahtarı kullanılıyor: "sites.site_not_found" bu durumu birebir anlatıyor
        // (alan adına bağlı IIS sitesi yok) ve çevirisi var. Yeni bir anahtar uydurmak, çevirisi
        // olmayan ham bir anahtarın ekranda görünmesi demekti; özgül açıklama Details'te taşınır.
        var site = await FindSiteAsync(alan, cancellationToken)
            ?? throw new AppException("sites.site_not_found", 409, 409,
                $"'{alan}' (ya da 'www.{alan}') adına bağlı bir IIS sitesi yok; otomatik yapılandırma yazılacak bir kök bulunamadı.");

        // 1) ARR/URL Rewrite ön koşulları. AYRI ÇAĞRI ve KİLİTTEN ÖNCE: bu metot kendi içinde IIS
        //    kuyruğunu alır, kilit ise yeniden girişe kapalıdır (bkz. IConcurrencyLock).
        await reverseProxyService.EnsureArrPrerequisitesAsync(site.Name, cancellationToken);

        // 2) Kuralları yaz. web.config, ters vekil servisiyle AYNI dosyanın AYNI bölgesidir →
        //    onunla aynı kuyruğu paylaşmak zorunda; ayrı bir kuyruk ikisinin aynı anda yazıp
        //    birinin değişikliğini kaybetmesine izin verirdi.
        var backendUrl = PanelBackendUrl.Resolve(configuration, runtimeOptions.Value);
        await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Iis, () =>
        {
            var yol = WebConfigPath(site);
            var belge = LoadWebConfig(yol);
            MailAutodiscoverRewriteRules.Apply(belge, alan, backendUrl);
            belge.Save(yol);
            return Task.CompletedTask;
        }, cancellationToken);

        // 3) Hizmet ana adlarını AYNI siteye bağla: aynı web.config, dolayısıyla aynı kurallar
        //    onları da karşılar. Ayrı bir site kurmak yeni sertifika + yeni kök demekti.
        foreach (var host in HizmetHostlari(alan))
        {
            await TryAddBindingAsync(site, host, cancellationToken);
        }

        // 4) DNS — sertifikadan ÖNCE: Let's Encrypt HTTP-01 doğrulaması adın çözülmesini ister.
        await TryPublishDnsAsync(site.Name, alan, cancellationToken);

        // 5) Sertifika (best-effort). Başarısız olması özelliği ÖLDÜRMEZ: Outlook'un ilk denediği
        //    adres zaten alan adının kendisidir ve onun sertifikası vardır.
        foreach (var host in HizmetHostlari(alan))
        {
            await TrySecureAsync(site.Name, host, contactEmail, cancellationToken);
        }

        var taze = await FindSiteAsync(alan, cancellationToken) ?? site;
        return await BuildReportAsync(alan, taze, cancellationToken);
    }

    // --- Geri alma ---

    /// <inheritdoc />
    public async Task<MailAutodiscoverPublicationDto> UnpublishAsync(string domain, CancellationToken cancellationToken = default)
    {
        var alan = NormalizeDomain(domain);
        var site = await FindSiteAsync(alan, cancellationToken);
        if (site is null)
        {
            // Site yoksa geri alınacak bir şey de yok; hata değil.
            return await BuildReportAsync(alan, null, cancellationToken);
        }

        await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Iis, () =>
        {
            var yol = WebConfigPath(site);
            if (!File.Exists(yol))
            {
                return Task.CompletedTask;
            }

            var belge = LoadWebConfig(yol);
            if (MailAutodiscoverRewriteRules.Remove(belge) > 0)
            {
                belge.Save(yol);
            }

            return Task.CompletedTask;
        }, cancellationToken);

        // YALNIZ panelin eklediği hizmet bağlamaları kaldırılır. Sitenin kendi alan adı ve www
        // bağlamalarına dokunulmaz — geri alma, siteyi yayından kaldırmak değildir.
        foreach (var host in HizmetHostlari(alan))
        {
            foreach (var baglama in site.Bindings.Where(item => Esit(item.HostName, host)).ToArray())
            {
                try
                {
                    await iisSiteService.RemoveBindingAsync(
                        site.Name,
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
                    logger.LogWarning(exception, "Otomatik yapılandırma bağlaması kaldırılamadı: {Site} → {Host}", site.Name, host);
                }
            }
        }

        var taze = await FindSiteAsync(alan, cancellationToken);
        return await BuildReportAsync(alan, taze, cancellationToken);
    }

    // --- Rapor ---

    private async Task<MailAutodiscoverPublicationDto> BuildReportAsync(
        string alan,
        SiteDto? site,
        CancellationToken cancellationToken)
    {
        var backendUrl = PanelBackendUrl.Resolve(configuration, runtimeOptions.Value);
        var adimlar = new List<MailAutodiscoverStepDto> { await MailAdimiAsync(alan, cancellationToken) };

        if (site is null)
        {
            adimlar.Add(Adim("site", "fail", string.Empty,
                $"'{alan}' ya da 'www.{alan}' adına bağlı bir IIS sitesi bulunamadı; istemci isteği düşecek bir kök yok."));
            adimlar.Add(Adim("rewrite", "unknown", string.Empty, "Site bulunmadığı için kural okunamadı."));
            adimlar.Add(Adim("binding", "unknown", string.Empty, "Site bulunmadığı için bağlamalar okunamadı."));
            adimlar.Add(Adim("https", "unknown", string.Empty, "Site bulunmadığı için sertifika bağlaması okunamadı."));
            adimlar.Add(Adim("dns", "unknown", string.Empty, "Site bulunmadığı için bölge okunamadı."));

            return new MailAutodiscoverPublicationDto
            {
                Domain = alan,
                BackendUrl = backendUrl,
                IsPublished = false,
                Steps = adimlar,
                ProbeUrls = MailAutodiscoverRewriteRules.ProbeUrls(alan),
                RequiredDnsRecords = GerekenDnsKayitlari(alan),
                CheckedAtUtc = DateTime.UtcNow
            };
        }

        adimlar.Add(Adim("site", "ok", site.Name, $"physicalPath={site.PhysicalPath}"));

        var (kuralAdimi, yayinda) = KuralAdimi(site, alan, backendUrl);
        adimlar.Add(kuralAdimi);
        adimlar.Add(BaglamaAdimi(site, alan));
        adimlar.Add(HttpsAdimi(site, alan));

        var (dnsAdimi, dnsYonetiliyor) = await DnsAdimiAsync(site.Name, alan, cancellationToken);
        adimlar.Add(dnsAdimi);

        return new MailAutodiscoverPublicationDto
        {
            Domain = alan,
            SiteName = site.Name,
            BackendUrl = backendUrl,
            IsPublished = yayinda,
            Steps = adimlar,
            ProbeUrls = MailAutodiscoverRewriteRules.ProbeUrls(alan),
            DnsManagedByPanel = dnsYonetiliyor,
            // Kayıtlar GERÇEKTEN yayınlanmışsa liste boştur. Ölçütün "bölge panelde mi" değil
            // "kayıt yayında mı" olması bilinçli: bölge panelde yönetiliyor olsa BİLE kayıt
            // düşmemiş olabilir (yazma başarısız olur, panelin dosyasında görünür, bölgede
            // görünmez). O durumda kullanıcıya boş liste göstermek, eksiği gizlemek olurdu.
            RequiredDnsRecords = dnsAdimi.Status == "ok" ? [] : GerekenDnsKayitlari(alan),
            CheckedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>Alan adı panelde gerçekten bir posta alanı mı ve ayarları ölçülebiliyor mu?</summary>
    private async Task<MailAutodiscoverStepDto> MailAdimiAsync(string alan, CancellationToken cancellationToken)
    {
        try
        {
            var ayarlar = await mailService.GetClientSettingsAsync(alan, cancellationToken);
            if (ayarlar is null)
            {
                // Uç, ayar ölçülemediğinde 404 döner. Vekili kursak bile istemci boş dönerdi;
                // bunu "kurulu" göstermek kullanıcıyı yanıltırdı.
                return Adim("mail", "fail", string.Empty,
                    $"'{alan}' panelde tanımlı bir posta alanı değil ya da istemci ayarları ölçülemedi; uç bu alana 404 döner.");
            }

            var sunucular = string.Join(", ", ayarlar.Servers.Select(item => $"{item.Protocol}:{item.Port}"));
            return Adim("mail", "ok", ayarlar.HostName, $"servers={sunucular}");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Posta istemci ayarları ölçülemedi: {Domain}", alan);
            return Adim("mail", "unknown", string.Empty, "Posta sunucusu ayarları okunamadı: " + exception.Message);
        }
    }

    private (MailAutodiscoverStepDto Step, bool Published) KuralAdimi(SiteDto site, string alan, string backendUrl)
    {
        var yol = WebConfigPath(site);
        if (!File.Exists(yol))
        {
            return (Adim("rewrite", "fail", "0", $"{yol} yok; otomatik yapılandırma kuralı yazılmamış."), false);
        }

        try
        {
            var belge = LoadWebConfig(yol);
            var sayi = MailAutodiscoverRewriteRules.Count(belge);
            var dogruHedef = MailAutodiscoverRewriteRules.TargetsBackend(belge, alan, backendUrl);

            if (sayi == 0)
            {
                return (Adim("rewrite", "fail", "0", $"{yol} içinde panele ait otomatik yapılandırma kuralı yok."), false);
            }

            if (sayi < MailAutodiscoverRewriteRules.ExpectedRuleCount)
            {
                return (Adim("rewrite", "fail", sayi.ToString(),
                    $"{MailAutodiscoverRewriteRules.ExpectedRuleCount} kuraldan {sayi} tanesi var; eksik olan istemciler ayar alamaz."), false);
            }

            if (!dogruHedef)
            {
                // Panelin portu ya da alan adı değişmiş: kural duruyor ama yanlış yere bakıyor.
                return (Adim("rewrite", "fail", sayi.ToString(),
                    $"Kurallar var ama hedefleri '{backendUrl}…?domain={alan}' ile eşleşmiyor; yeniden yayınlanmalı."), false);
            }

            return (Adim("rewrite", "ok", sayi.ToString(), $"target={backendUrl} (web.config={yol})"), true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "web.config okunamadı: {Path}", yol);
            return (Adim("rewrite", "unknown", string.Empty, "web.config okunamadı: " + exception.Message), false);
        }
    }

    private static MailAutodiscoverStepDto BaglamaAdimi(SiteDto site, string alan)
    {
        var eksik = HizmetHostlari(alan)
            .Where(host => !site.Bindings.Any(binding => Esit(binding.HostName, host)))
            .ToArray();

        if (eksik.Length == 0)
        {
            return Adim("binding", "ok", string.Join(", ", HizmetHostlari(alan)), "Hizmet ana adları siteye bağlı.");
        }

        // UYARI, HATA DEĞİL: Outlook'un ilk denediği adres alan adının KENDİSİDİR; hizmet ana
        // adları yalnız ikinci/üçüncü denemeler ve Thunderbird'ün klasik yolu için gerekir.
        return Adim("binding", "warn", string.Join(", ", eksik),
            "Bu ana adlar siteye bağlı değil; Outlook'un ilk denemesi (alan adının kendisi) yine çalışır, ikinci denemesi çalışmaz.");
    }

    private static MailAutodiscoverStepDto HttpsAdimi(SiteDto site, string alan)
    {
        var httpsHostlari = site.Bindings
            .Where(binding => string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase))
            .Select(binding => (binding.HostName ?? string.Empty).Trim().ToLowerInvariant())
            .ToArray();

        // Alan adının KENDİSİ HTTPS olmalı: autodiscover istemcileri sertifikayı doğrular ve
        // doğrulama başarısızsa AYARI HİÇ ALMAZ (uyarı gösterip devam etmezler).
        if (httpsHostlari.Any(host => host == alan || host.Length == 0))
        {
            return Adim("https", "ok", alan, $"httpsBindings={string.Join(", ", httpsHostlari.Where(item => item.Length > 0))}");
        }

        return Adim("https", "fail", string.Empty,
            $"'{alan}' için HTTPS bağlaması yok. Otomatik yapılandırma istemcileri sertifikayı doğrular; sertifikasız adresten ayar ALMAZLAR.");
    }

    private async Task<(MailAutodiscoverStepDto Step, bool Managed)> DnsAdimiAsync(
        string siteName,
        string alan,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string>? yayinlanan;
        try
        {
            yayinlanan = await dnsService.GetPublishedRecordKeysAsync(siteName, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DNS bölgesi okunamadı: {Site}", siteName);
            yayinlanan = null;
        }

        if (yayinlanan is null)
        {
            // ÖLÇEMEDİK ≠ EKSİK YOK. Alan adı büyük olasılıkla dış bir DNS sağlayıcısında;
            // kullanıcıya ne ekleyeceğini söyleriz, "yapıldı" demeyiz.
            return (Adim("dns", "warn", string.Empty,
                "Bölge bu sunucudaki Windows DNS'ten okunamadı; kayıtları kendi DNS sağlayıcınızda oluşturmanız gerekir (gerekli kayıtlar listede)."), false);
        }

        var eksik = HizmetOnekleri
            .Select(onek => onek.TrimEnd('.'))
            .Where(etiket => !yayinlanan.Any(anahtar =>
                anahtar.EndsWith("|" + etiket, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (eksik.Length == 0)
        {
            return (Adim("dns", "ok", string.Join(", ", HizmetHostlari(alan)), "Kayıtlar bölgede yayınlanmış."), true);
        }

        return (Adim("dns", "warn", string.Join(", ", eksik),
            "Bu kayıtlar bölgede yok. Outlook'un ilk denemesi (alan adının kendisi) kayıt gerektirmez, ikinci denemesi gerektirir."), true);
    }

    // --- Yardımcı işler ---

    private async Task TryAddBindingAsync(SiteDto site, string host, CancellationToken cancellationToken)
    {
        if (site.Bindings.Any(binding => Esit(binding.HostName, host)))
        {
            return; // idempotent
        }

        try
        {
            await iisSiteService.AddBindingAsync(
                site.Name,
                new SiteBindingRequest { Protocol = "http", HostName = host, IpAddress = "*", Port = 80 },
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Ad başka bir siteye bağlıysa IIS reddeder. MEVCUT BAĞLAMAYI ZORLA ALMAYIZ: o, başka
            // bir müşterinin adresini ele geçirmek olurdu. Durum raporunda "warn" olarak görünür.
            logger.LogWarning(exception, "Otomatik yapılandırma bağlaması eklenemedi: {Site} → {Host}", site.Name, host);
        }
    }

    private async Task TryPublishDnsAsync(string siteName, string alan, CancellationToken cancellationToken)
    {
        try
        {
            // CNAME (A değil): alan adının A kaydı değiştiğinde hizmet adları KENDİLİĞİNDEN takip
            // eder. Panelin yerleşik şablonu www/ftp için de tam olarak bunu yapıyor.
            var kayitlar = GerekenDnsKayitlari(alan)
                .Select(kayit => new UpsertDnsRecordRequest
                {
                    Type = kayit.Type,
                    Host = kayit.Host,
                    Value = kayit.Value,
                    Ttl = 3600
                })
                .ToArray();

            await dnsService.UpsertRecordsAsync(siteName, kayitlar, cancellationToken);
        }
        catch (Exception exception)
        {
            // Bölge panelde yönetilmiyorsa burası patlar; bu bir hata değil, bir DURUMDUR ve
            // rapordaki dns adımı kullanıcıya ne yapması gerektiğini söyler.
            logger.LogWarning(exception, "Otomatik yapılandırma DNS kayıtları yazılamadı: {Site}", siteName);
        }
    }

    private async Task TrySecureAsync(string siteName, string host, string contactEmail, CancellationToken cancellationToken)
    {
        try
        {
            await sslService.TrySecureHostAsync(siteName, host, contactEmail ?? string.Empty, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Otomatik yapılandırma ana adı HTTPS yapılamadı: {Host}", host);
        }
    }

    /// <summary>Kullanıcının (ya da panelin) oluşturması gereken kayıtlar — tek kaynak.</summary>
    private static IReadOnlyList<MailAutodiscoverDnsRecordDto> GerekenDnsKayitlari(string alan) =>
        [.. HizmetOnekleri.Select(onek => new MailAutodiscoverDnsRecordDto
        {
            Type = "CNAME",
            Host = onek.TrimEnd('.'),
            Value = alan
        })];

    private static IReadOnlyList<string> HizmetHostlari(string alan)
        => [.. HizmetOnekleri.Select(onek => onek + alan)];

    /// <summary>
    /// Alan adını 80/443'ten servis eden site. Eşleşme alan adının kendisi ya da <c>www.</c>
    /// biçimidir; hizmet ana adları (autodiscover./autoconfig.) da kabul edilir ki yayın
    /// yapılmış bir alan yeniden ölçülebilsin.
    /// </summary>
    private async Task<SiteDto?> FindSiteAsync(string alan, CancellationToken cancellationToken)
    {
        var siteler = await iisSiteService.GetSitesAsync(cancellationToken);
        var adaylar = new[] { alan, "www." + alan }.Concat(HizmetOnekleri.Select(onek => onek + alan)).ToArray();

        // Sıra önemli: önce alan adının KENDİSİNE bağlı site aranır. Yalnız hizmet adına bağlı bir
        // site bulunup asıl site atlanırsa kurallar yanlış köke yazılırdı.
        foreach (var aday in adaylar)
        {
            var site = siteler.FirstOrDefault(item =>
                item.Bindings.Any(binding => Esit(binding.HostName, aday)));
            if (site is not null)
            {
                return site;
            }
        }

        return null;
    }

    private static string WebConfigPath(SiteDto site)
    {
        // PhysicalPath (ApplicationRootPath DEĞİL): IIS kuralları sitenin KÖKÜNDEN okur.
        // ApplicationRootPath, Laravel gibi düzenlerde belge kökünün ÜSTÜNÜ gösterebilir ve
        // oraya yazılan bir web.config IIS tarafından hiç okunmaz.
        var kok = Environment.ExpandEnvironmentVariables((site.PhysicalPath ?? string.Empty).Trim());
        if (kok.Length == 0)
        {
            throw new AppException("sites.invalid_physical_path", 400, 400);
        }

        Directory.CreateDirectory(kok);
        return Path.Combine(Path.GetFullPath(kok), "web.config");
    }

    private static XDocument LoadWebConfig(string path)
        => File.Exists(path)
            ? XDocument.Load(path, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("configuration"));

    /// <summary>
    /// Alan adı web.config'e ve IIS bağlamasına gider → KATI doğrulama. Joker/boşluk/dizin
    /// karakteri kabul edilmez; kural metnine kaçış gerektiren hiçbir şey giremez.
    /// </summary>
    internal static string NormalizeDomain(string? domain)
    {
        var deger = (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (!DomainPattern().IsMatch(deger))
        {
            // Alan adı burada bir IIS BAĞLAMA ana adı olarak kullanılıyor; mevcut ve çevirisi olan
            // anahtar bu durumu birebir karşılıyor.
            throw new AppException("sites.invalid_host_name", 400, 400, $"'{domain}' geçerli bir alan adı değil.");
        }

        return deger;
    }

    private static MailAutodiscoverStepDto Adim(string id, string status, string value, string detail)
        => new() { Id = id, Status = status, Value = value, Detail = detail };

    private static bool Esit(string? a, string? b)
        => string.Equals((a ?? string.Empty).Trim().TrimEnd('.'), (b ?? string.Empty).Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))+$", RegexOptions.Compiled)]
    private static partial Regex DomainPattern();
}
