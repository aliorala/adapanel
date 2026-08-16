using System.Text;
using System.Xml;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Posta istemcisi otomatik yapılandırma uçları: Outlook <c>autodiscover.xml</c> ve
/// Thunderbird <c>config-v1.1.xml</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir denetleyici:</b> bu uçlar KİMLİK DOĞRULAMASIZ çağrılır — istemci, kullanıcı
/// daha hesabını kurmadan önce sorar. <see cref="MailController"/> ise bütünüyle
/// <c>[Authorize]</c> ve alt-kullanıcı bölüm filtresi altındadır; anonim uçları oraya karıştırmak
/// o sınıfın "her şey kimlikli" değişmezini bozardı. Ayrıca burada durum DEĞİŞMEZ, dolayısıyla
/// denetim kaydı da yoktur.
/// </para>
/// <para>
/// <b>Güvenlik sınırı:</b> yanıt yalnızca PANELDE TANIMLI alan adları için üretilir; tanımsız
/// alan 404 alır. Yanıtta hesap listesi, kullanıcı bilgisi ya da sır yoktur — yalnız sunucu adı,
/// portlar ve şifreleme biçimi, hepsi panelin gerçek posta yapılandırmasından ölçülerek.
/// </para>
/// <para>
/// <b>Antiforgery:</b> Outlook bu ucu POST ile çağırır ve çerez taşımaz; küresel antiforgery
/// filtresi burada koruma değil yalnız engel olurdu (CSRF koruması ÇEREZE karşıdır).
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class MailAutodiscoverController(IMailService mailService) : ControllerBase
{
    /// <summary>
    /// Outlook isteğinin gövdesi için üst sınır.
    /// </summary>
    /// <remarks>
    /// Gerçek autodiscover istekleri birkaç yüz bayttır. Sınırsız okumak, kimlik doğrulamasız bir
    /// uçta belleği tüketmenin en ucuz yolunu bırakırdı.
    /// </remarks>
    private const int AzamiGovdeBoyutu = 64 * 1024;

    /// <summary>Outlook autodiscover (POST: istemcinin normal çağrısı).</summary>
    [HttpPost("/autodiscover/autodiscover.xml")]
    public async Task<IActionResult> OutlookPostAsync(CancellationToken cancellationToken)
    {
        var adres = await ReadRequestedAddressAsync(cancellationToken);
        return await OutlookAsync(adres, cancellationToken);
    }

    /// <summary>Outlook autodiscover (GET: bazı istemciler ve elle doğrulama için).</summary>
    [HttpGet("/autodiscover/autodiscover.xml")]
    public Task<IActionResult> OutlookGetAsync([FromQuery(Name = "emailaddress")] string? emailAddress, CancellationToken cancellationToken)
        => OutlookAsync(emailAddress, cancellationToken);

    /// <summary>
    /// Thunderbird autoconfig. İki yol da yayınlanır: alan adının kendi kökündeki
    /// <c>.well-known</c> yolu ve <c>autoconfig.&lt;alan&gt;</c> ana adındaki klasik yol.
    /// İstemci ikisini de dener; yalnız birini yayınlamak, istemcinin diğerini denerken
    /// zaman aşımına düşmesine yol açar.
    /// </summary>
    [HttpGet("/.well-known/autoconfig/mail/config-v1.1.xml")]
    [HttpGet("/mail/config-v1.1.xml")]
    public async Task<IActionResult> ThunderbirdAsync([FromQuery(Name = "emailaddress")] string? emailAddress, CancellationToken cancellationToken)
    {
        var domain = ResolveDomain(emailAddress);
        var settings = domain is null ? null : await mailService.GetClientSettingsAsync(domain, cancellationToken);
        if (settings is null)
        {
            return NotFound();
        }

        return Xml(MailAutodiscoverBuilder.BuildAutoconfigXml(settings));
    }

    private async Task<IActionResult> OutlookAsync(string? emailAddress, CancellationToken cancellationToken)
    {
        var domain = ResolveDomain(emailAddress);
        var settings = domain is null ? null : await mailService.GetClientSettingsAsync(domain, cancellationToken);
        if (settings is null)
        {
            return NotFound();
        }

        return Xml(MailAutodiscoverBuilder.BuildOutlookXml(settings, emailAddress));
    }

    private ContentResult Xml(string xml) => Content(xml, "application/xml", Encoding.UTF8);

    /// <summary>
    /// Hangi alan adı soruluyor?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sıra: (1) istemcinin bildirdiği e-posta adresinin alan adı, (2) isteği ileten VEKİLİN
    /// açıkça yazdığı <c>domain</c> parametresi, (3) isteğin ana adı. Ana addaki hizmet öneki
    /// (<c>autodiscover.</c>/<c>autoconfig.</c>) atılır — istemci uçlara tam olarak bu adlardan
    /// gelir ve "autodiscover.example.com" panelde tanımlı bir posta alanı değildir.
    /// </para>
    /// <para>
    /// <b>(2) neden var:</b> istekler artık müşterinin IIS sitesinden ARR ile buraya iletiliyor
    /// (bkz. <c>MailAutodiscoverPublisher</c>). ARR arka uca giderken Host başlığını hedefin adıyla
    /// (<c>localhost</c>) değiştirir; ana ada bakan bir çözümleme her istekte 404 dönerdi. Vekil
    /// kuralı alan adını zaten bildiği için onu açıkça yazar.
    /// </para>
    /// <para>
    /// <b>Güvenlik:</b> bu parametre isteyen tarafından uydurulabilir — ama ana ad da öyle.
    /// Yanıt yalnız PANELDE TANIMLI alanlar için üretilir ve sır taşımaz; yeni bir yüzey yok.
    /// </para>
    /// </remarks>
    internal static string? ResolveDomainFrom(string? emailAddress, string? requestHost, string? proxyDomain = null)
    {
        var adres = (emailAddress ?? string.Empty).Trim();
        var at = adres.IndexOf('@');
        if (at > 0 && at < adres.Length - 1)
        {
            return Normalize(adres[(at + 1)..]);
        }

        var vekilden = Normalize(proxyDomain);
        if (vekilden is not null)
        {
            return vekilden;
        }

        var host = Normalize(requestHost);
        if (host is null)
        {
            return null;
        }

        foreach (var onek in new[] { "autodiscover.", "autoconfig.", "mail." })
        {
            if (host.StartsWith(onek, StringComparison.Ordinal) && host.Length > onek.Length)
            {
                return host[onek.Length..];
            }
        }

        return host;
    }

    /// <remarks>
    /// <c>domain</c> sorgu parametresi metot imzasına DEĞİL buraya okunuyor: uçların imzaları
    /// istemcilerin gerçekten gönderdiği alanları (yalnız <c>emailaddress</c>) yansıtmalı; vekilin
    /// eklediği iç parametre onların yanında bir istemci alanı gibi durmamalı.
    /// </remarks>
    private string? ResolveDomain(string? emailAddress)
        => ResolveDomainFrom(emailAddress, Request.Host.Host, Request.Query["domain"].ToString());

    private static string? Normalize(string? value)
    {
        var deger = (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        return deger.Length == 0 || !deger.Contains('.') ? null : deger;
    }

    /// <summary>Outlook POST gövdesinden <c>EMailAddress</c> alanını okur.</summary>
    /// <remarks>
    /// XML dış varlık çözümlemesi (DTD/XXE) KAPALI okunur: kimlik doğrulamasız bir uçta yabancı
    /// XML ayrıştırmak, sunucu dosyalarını dışarı sızdırmanın klasik yoludur.
    /// </remarks>
    private async Task<string?> ReadRequestedAddressAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var okuyucu = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var tampon = new char[AzamiGovdeBoyutu];
            var okunan = await okuyucu.ReadBlockAsync(tampon, cancellationToken);
            if (okunan <= 0)
            {
                return null;
            }

            using var xml = XmlReader.Create(
                new StringReader(new string(tampon, 0, okunan)),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });

            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element
                    && string.Equals(xml.LocalName, "EMailAddress", StringComparison.OrdinalIgnoreCase))
                {
                    return xml.ReadElementContentAsString();
                }
            }
        }
        catch (XmlException)
        {
            // Bozuk gövde: adres bilinmiyor demektir, hata değil. Ana addan devam edilir.
        }

        return null;
    }
}

/// <summary>
/// Otomatik yapılandırmayı müşterinin alan adına BAĞLAYAN yönetim uçları.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden aynı dosyada ama AYRI sınıf:</b> yukarıdaki denetleyicinin değişmezi "her uç
/// kimlik doğrulamasız"dır ve istemciler oraya sabit adreslerden gelir. Yönetim eylemlerini
/// oraya karıştırmak o değişmezi bozardı. Ayrı sınıf, ayrı yetki kapısı.
/// </para>
/// <para>
/// <b>Neden yönetici:</b> bu eylem müşterinin IIS sitesinin <c>web.config</c>'ine yazar, IIS
/// bağlaması ekler, DNS bölgesine kayıt basar ve sertifika ister. Bunların hiçbiri kiracı
/// yetkisi değildir.
/// </para>
/// <para>
/// <b>Neden otomatik değil, açık eylem:</b> işlem sunucu geneli bileşen (ARR) kurabilir,
/// sertifika otoritesine istek yapar ve müşterinin <c>web.config</c>'ini değiştirir. Bunu her
/// alan adı eklendiğinde sessizce yapmak, kullanıcının görmediği ve geri alamadığı bir yan etki
/// olurdu. Uçlar idempotenttir; <c>DELETE</c> yapılanı tam olarak geri alır ve durum ucu
/// arayüzün "kurulu değil" diyebilmesi için her an ölçüm verir.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/mail/autoconfig")]
public sealed class MailAutodiscoverSetupController(
    IMailAutodiscoverPublisher publisher,
    IAuditService auditService) : ControllerBase
{
    /// <summary>Zinciri ölçer; hiçbir şey yazmaz.</summary>
    [HttpGet("{domain}")]
    public async Task<ActionResult<ApiResponse<MailAutodiscoverPublicationDto>>> GetAsync(
        string domain,
        CancellationToken cancellationToken)
        => Ok(ApiResponse<MailAutodiscoverPublicationDto>.Ok(await publisher.GetStatusAsync(domain, cancellationToken)));

    /// <summary>Otomatik yapılandırmayı alan adının servis edildiği siteye bağlar.</summary>
    [HttpPost("{domain}")]
    public async Task<ActionResult<ApiResponse<MailAutodiscoverPublicationDto>>> PublishAsync(
        string domain,
        [FromQuery] string? contactEmail,
        CancellationToken cancellationToken)
    {
        var report = await publisher.PublishAsync(domain, contactEmail ?? string.Empty, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "mail.autoconfig.published",
            report.Domain,
            $"Autodiscover/autoconfig published on site '{report.SiteName}' (reachable: {report.IsPublished}).");

        return Ok(ApiResponse<MailAutodiscoverPublicationDto>.Ok(
            report,
            report.IsPublished ? "mail.autoconfig_published" : "mail.autoconfig_published_partial"));
    }

    /// <summary>Yayını geri alır: panelin yazdığı kurallar ve eklediği bağlamalar kaldırılır.</summary>
    [HttpDelete("{domain}")]
    public async Task<ActionResult<ApiResponse<MailAutodiscoverPublicationDto>>> UnpublishAsync(
        string domain,
        CancellationToken cancellationToken)
    {
        var report = await publisher.UnpublishAsync(domain, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "mail.autoconfig.unpublished",
            report.Domain,
            "Autodiscover/autoconfig rules and service bindings were removed.");

        return Ok(ApiResponse<MailAutodiscoverPublicationDto>.Ok(report, "mail.autoconfig_unpublished"));
    }
}
