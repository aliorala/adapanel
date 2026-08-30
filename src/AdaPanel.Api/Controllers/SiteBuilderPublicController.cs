using AdaPanel.Api.Security;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.SiteBuilder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Site kurucunun ürettiği İLETİŞİM FORMUNUN gönderim ucu. Kimliksiz.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden kimliksiz olmak ZORUNDA:</b> formu dolduran kişi müşterinin sitesini ziyaret eden
/// biridir; panelin kullanıcısı değildir, olamaz da. Ondan panele giriş yapmasını istemek
/// özelliğin kendisini imkânsız kılardı. Aynı gerekçe <c>MailListPublicController</c>'da yazılı
/// ve bu uç onunla aynı disiplini izliyor.
/// </para>
/// <para>
/// <b>Yüzey KASITEN dar:</b> tek bir şey yapar — sitenin SAHİBİNİN kaydettiği adrese bir posta
/// gönderir. Site listelemez, tasarım okutmaz, "bu site var mı" sorusunu CEVAPLAMAZ: form kapalı
/// olan bir site ile hiç olmayan bir site AYNI sayfayı döner. Aksi hâlde uç, sunucudaki siteleri
/// taramaya yarayan bir araca dönerdi.
/// </para>
/// <para>
/// <b>Neden bir SPAM SİLAHI değil:</b> alıcı adresi ziyaretçiden ALINMAZ (site sahibinin
/// tasarımından okunur), ziyaretçinin yazdığı hiçbir şey posta BAŞLIĞINA girmez (konu
/// <c>SiteBuilderContactForm.HeaderSafe</c>'ten geçer), IP başına + site başına hız sınırı ve
/// gönderen başına bekleme süresi var, ve görünmez bir bal küpü alanı bulunuyor. Dış bir servise
/// (CAPTCHA) bağımlılık YOK — panelin hiçbir yeri ziyaretçiyi üçüncü bir sunucuya bağlamıyor.
/// </para>
/// <para>
/// <b>Neden JSON değil HTML:</b> bu ucun tek istemcisi bir İNSANIN TARAYICISIDIR ve istek,
/// müşterinin sitesindeki bir <c>&lt;form&gt;</c>'un düz POST'udur. Tarayıcı bunu bir gezinme
/// olarak yapar; cevap da bir sayfa olmalıdır. Abonelikten çıkma akışında JSON beklemek tam da
/// bu yüzden canlıda kırılmıştı.
/// </para>
/// <para>
/// <b>Antiforgery:</b> <see cref="IgnoreAntiforgeryTokenAttribute"/> gerekli — istek panelin
/// SPA'sinden değil, BAŞKA BİR KÖKENDEKİ (müşterinin alan adı) bir formdan gelir ve CSRF çerezi
/// taşımaz. CSRF'in koruduğu şey burada zaten yok: uç kimlik doğrulamaz, dolayısıyla "kurbanın
/// oturumunu kullanmak" diye bir şey mümkün değildir.
/// </para>
/// <para>
/// <b>Kimliksiz ama KİRACIYA AİT.</b> Kaydın <c>UserId</c>'si yapısal olarak boştur (aktör yok);
/// kaydın site sahibinin denetim ekranında görünmesinin TEK yolu KONUSUDUR. Konu, site adından
/// <see cref="AuditSubject.SiteOwnerAsync"/> ile ÖLÇÜLÜR. Yazılmasaydı site sahibi kendi formuna
/// gelen gönderimleri hiç göremezdi — oysa "formuma kim ne zaman yazdı" tam da bu kaydın var
/// olma sebebi.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/v1/public/site-forms")]
public sealed class SiteBuilderPublicController(
    ISiteBuilderContactService contactService,
    ICustomerScopeService customerScopes,
    IAuditService auditService,
    ILogger<SiteBuilderPublicController> logger) : ControllerBase
{
    private const string HtmlContentType = "text/html; charset=utf-8";

    /// <summary>
    /// Formu gönderir. Sonuç ziyaretçiye AÇIKÇA söylenir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Yanıt durum kodu daima 200.</b> Bu bilinçli ve gerekçesi ziyaretçi tarafında: cevap bir
    /// SAYFADIR ve tarayıcı onu gösterecektir. 4xx/5xx döndürseydik bazı tarayıcılar ve araya
    /// giren vekiller kendi hata sayfalarını basar, ziyaretçi de bizim yazdığımız "mesajınız
    /// GÖNDERİLMEDİ, şu adresi kullanın" açıklamasını hiç görmezdi — yani dürüstlük kuralı tam da
    /// başarısızlık anında işlemezdi. Başarı/başarısızlık ayrımı sayfanın METNİNDE, açıkça yazılı.
    /// </para>
    /// <para>
    /// <b>Denetim kaydı yalnız bir posta DENENDİĞİNDE yazılır.</b> Uç kimliksiz ve tarama
    /// hedefidir; her biçimsiz POST'u kayda yazmak, günlüğü şişirip asıl olayları görünmez
    /// kılardı — denetim kaydının değeri seyrekliğindedir. Reddedilen istekler servis katmanında
    /// UYARI olarak günlüğe düşer.
    /// </para>
    /// <para>
    /// <b>Ziyaretçinin yazdığı hiçbir şey kayda GİRMEZ</b> (ad, adres, mesaj). Kayıt, korumaya
    /// çalıştığımız verinin ikinci kopyası olmamalı; aynı kural abonelikten çıkma ucunda da
    /// uygulanmış durumda. Kayda giren: hangi site, sonuç ne, hangi IP.
    /// </para>
    /// </remarks>
    // HIZ SINIRI — İKİNCİ, DIŞ KATMAN. Servis katmanında zaten IP/site/gönderen sınırları var
    // (SiteBuilderContactRateLimiter, IP başına 5/15dk). O sınır MVC boru hattının İÇİNDE, yani
    // reddedilen istek bile model bağlamayı, form ayrıştırmayı ve servis çözümlemeyi yaptırıyor.
    // PublicMailSend (10/15dk) ondan GEVŞEK bir dış tavandır: iç sınırın davranışını DEĞİŞTİRMEZ
    // (5 < 10, yani reddi hâlâ iç sınır verir ve mesajı da o üretir), ama sınırsız sayıda isteğin
    // boru hattına GİRMESİNİ keser. İki sınırın da tek kaynaktan okunması şart değil; farklı
    // katmanlarda farklı soruları kapatıyorlar ve ilişkileri burada YAZILI.
    [EnableRateLimiting(PanelRateLimits.PublicMailSend)]
    [RequestSizeLimit(PanelServerLimits.PublicFormMaxBytes)]
    [HttpPost("{siteKey}/contact")]
    public async Task<ContentResult> SubmitContactAsync(
        string siteKey,
        [FromForm] string? name,
        [FromForm] string? email,
        [FromForm] string? subject,
        [FromForm] string? message,
        [FromForm] string? website,
        CancellationToken cancellationToken)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        SiteBuilderContactResult result;
        try
        {
            result = await contactService.SubmitAsync(
                siteKey,
                // `website` BAL KÜPÜ alanıdır; adı bilerek masum. Değeri doluysa posta gönderilmez.
                new SiteBuilderContactSubmission(name, email, subject, message, website),
                ip,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // BEKLENMEYEN ARIZA DA ZİYARETÇİYE SÖYLENİR. Bu blok olmasaydı panelin genel hata
            // katmanı devreye girer ve bir TARAYICIYA ham JSON basardı: ziyaretçi mesajının
            // gidip gitmediğini anlayamazdı. Yakalama, başarısızlığı başarıya ÇEVİREMEZ — tek
            // yaptığı, aynı başarısızlığı okunur bir sayfayla anlatmak.
            logger.LogError(exception, "Site kurucu iletisim formu istegi islenemedi.");
            result = new SiteBuilderContactResult(SiteBuilderContactOutcome.SendFailed, string.Empty);
        }

        if (result.Outcome is SiteBuilderContactOutcome.Sent or SiteBuilderContactOutcome.SendFailed)
        {
            try
            {
                await auditService.WriteForSubscriptionAsync(
                    "sitebuilder.contact.submitted",
                    SafeResource(result.SiteName),
                    $"Anonymous contact form submission (delivered: {result.Outcome == SiteBuilderContactOutcome.Sent}).",
                    userId: null,
                    await AuditSubject.SiteOwnerAsync(customerScopes, result.SiteName, cancellationToken),
                    ip,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                // KAYIT YAZILAMADI ama POSTA ZATEN GİTTİ. Burada patlamak, gerçekten gönderilmiş
                // bir mesaj için ziyaretçiye "gönderilemedi" demek olurdu — yani kaydı zenginleştirme
                // çabası, kullanıcıya YALAN olarak yansırdı. Arıza günlüğe düşer; ziyaretçiye
                // söylenen şey, postanın gerçek sonucudur.
                logger.LogError(exception, "Site kurucu iletisim formu denetim kaydi yazilamadi.");
            }
        }

        return new ContentResult
        {
            Content = SiteBuilderContactPages.For(result.Outcome),
            ContentType = HtmlContentType,
            StatusCode = StatusCodes.Status200OK
        };
    }

    /// <summary>
    /// Kayda yazılacak kaynak adı. Site adı panelin KENDİ kaydından geliyor (istekten değil), ama
    /// yine de sınırlanır: uzunluk sınırı, kayıt görünümünü şişiren her yolu tek yerde kapatır.
    /// </summary>
    private static string SafeResource(string? siteName)
    {
        var trimmed = (siteName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "site-form";
        }

        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }
}
