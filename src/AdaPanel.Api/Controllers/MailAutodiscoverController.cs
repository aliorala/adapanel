using System.Text;
using System.Xml;
using AdaPanel.Core.Interfaces;
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
    /// Önce istemcinin bildirdiği e-posta adresinin alan adı; yoksa isteğin ana adı. Ana addaki
    /// hizmet öneki (<c>autodiscover.</c>/<c>autoconfig.</c>) atılır — istemci uçlara tam olarak
    /// bu adlardan gelir ve "autodiscover.example.com" panelde tanımlı bir posta alanı değildir.
    /// </remarks>
    internal static string? ResolveDomainFrom(string? emailAddress, string? requestHost)
    {
        var adres = (emailAddress ?? string.Empty).Trim();
        var at = adres.IndexOf('@');
        if (at > 0 && at < adres.Length - 1)
        {
            return Normalize(adres[(at + 1)..]);
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

    private string? ResolveDomain(string? emailAddress) => ResolveDomainFrom(emailAddress, Request.Host.Host);

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
