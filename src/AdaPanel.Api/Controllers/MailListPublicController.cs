using AdaPanel.Api.Security;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Dağıtım listesinden self-servis çıkma. Panelin TEK kimliksiz durum değiştiren ucu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden kimliksiz olmak ZORUNDA:</b> listeden çıkmak isteyen kişi panelin kullanıcısı değildir —
/// çoğu zaman bambaşka bir alan adındaki bir aboneden ibarettir. Ondan panele giriş yapmasını
/// istemek, özelliğin kendisini imkânsız kılardı. Plesk/Mailman'da da bu sayfa herkese açıktır.
/// </para>
/// <para>
/// <b>Neden JSON değil HTML:</b> bu ucun tek istemcisi bir İNSANIN TARAYICISIDIR — akış
/// e-postadaki bir bağlantıya tıklamakla başlar. İlk yazımda uçlar <c>[FromBody]</c> JSON
/// bekliyordu; e-postadaki bağlantıya tıklayan tarayıcı GET yapar, yani akış 405 ile ÖLÜRDÜ.
/// Uçlar artık tarayıcının gerçekten yaptığı şeyi kabul ediyor: GET ile sayfa, form POST ile işlem.
/// </para>
/// <para>
/// <b>Yüzey KASITEN dar:</b> yalnızca bir adresi bir listeden çıkarır. Liste listelemez, abone
/// göstermez, "bu adres abone mi" sorusunu CEVAPLAMAZ — istek adımı, adres abone olsun ya da
/// olmasın AYNI sayfayı döner. Aksi hâlde uç bir E-POSTA HASAT aracına dönerdi.
/// </para>
/// <para>
/// <b>Antiforgery:</b> <see cref="IgnoreAntiforgeryTokenAttribute"/> gerekli — istek panelin
/// SPA'sinden değil, e-postadaki bağlantıdan/genel bir formdan gelir ve CSRF çerezi taşımaz.
/// CSRF'in koruduğu şey burada zaten yok: uç kimlik doğrulamaz, dolayısıyla "kurbanın oturumunu
/// kullanmak" mümkün değildir.
/// </para>
/// <para>
/// <b>HIZ SINIRI — bu sınıfın uçları İKİ AYRI politikaya bölündü ve bölünme kasıtlıdır.</b>
/// <see cref="RequestAsync"/> POSTA GÖNDERTİR: kimlik doğrulamasız bir istekle sunucunun giden
/// kuyruğunu doldurup İTİBARINI yakmanın en ucuz yolu buydu ve buranın hiçbir sınırı yoktu
/// (küresel sınırlayıcı yalnız <c>Authorization</c> başlığı taşıyanları bölümlüyordu, bu yol
/// "no limiter"a düşüyordu). Diğer üç uç posta göndermez; onları aynı dar kovaya koymak,
/// isteğini yenileyerek bütçesini tüketen GERÇEK kullanıcıyı kendi onay bağlantısından dışarıda
/// bırakırdı — yani düzeltme özelliği kırardı. Değerler ve gerekçeleri
/// <see cref="PanelRateLimits"/>'te (tek kaynak).
/// </para>
/// <para>
/// <b>Kimliksiz ama KİRACIYA AİT.</b> Kayıtların <c>UserId</c>'si yapısal olarak boştur (aktör
/// yok) — bu yüzden kaydın kiracının görünümüne düşmesinin TEK yolu konusudur. Konu da boş
/// bırakılsaydı, <c>AuditLogQueryEngine.ApplyVisibility</c>'nin iki koşulu da elerdi ve dağıtım
/// listesinin sahibi, kendi listesinden birinin çıkarıldığını kendi denetim ekranında HİÇ
/// göremezdi. Konu, listenin ALAN ADINDAN ölçülür (bkz. <see cref="ListOwnerAsync"/>).
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/v1/public/mail-lists")]
public sealed class MailListPublicController(
    MailListUnsubscribeService unsubscribeService,
    ICustomerScopeService customerScopes,
    IIisSiteService iisSiteService,
    IPanelPreferencesService preferencesService,
    IAuditService auditService,
    ILogger<MailListPublicController> logger) : ControllerBase
{
    private const string HtmlContentType = "text/html; charset=utf-8";

    /// <summary>Birinci adım (sayfa): abone kendi adresini yazar. DURUM DEĞİŞTİRMEZ.</summary>
    [EnableRateLimiting(PanelRateLimits.PublicRead)]
    [HttpGet("unsubscribe")]
    public ContentResult RequestForm([FromQuery] string? list) =>
        Html(MailListUnsubscribePages.RequestForm(list ?? string.Empty, PathOf("unsubscribe")));

    /// <summary>
    /// Birinci adım (işlem): onay postası ister. Cevap, adres abone olsun ya da olmasın AYNIDIR.
    /// </summary>
    [EnableRateLimiting(PanelRateLimits.PublicMailSend)]
    [RequestSizeLimit(PanelServerLimits.PublicFormMaxBytes)]
    [HttpPost("unsubscribe")]
    public async Task<ContentResult> RequestAsync(
        [FromForm] string? list,
        [FromForm] string? email,
        CancellationToken cancellationToken)
    {
        var sent = false;
        try
        {
            sent = await unsubscribeService.RequestAsync(
                list ?? string.Empty,
                email ?? string.Empty,
                await ConfirmUrlTemplateAsync(cancellationToken),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // HATA DA SIZDIRILMAZ: posta gönderilemedi ya da liste yok — ikisi de aynı sayfayı
            // verir, yoksa saldırgan hata farkından adresin/listenin varlığını çıkarabilirdi.
            logger.LogWarning(exception, "Abonelikten çıkma isteği işlenemedi.");
        }

        // Kimliksiz uçtan gelen HER istek kayda geçer (userId yok, IP var). İstenen ADRES kayda
        // GİRMEZ — kayıt, korumaya çalıştığımız adres listesinin ikinci kopyası olmamalı.
        //
        // DERİN SAHİPLİK ARAMASI YALNIZ `sent` DOĞRUYKEN: bu uçta `list` alanı tamamen İSTEMCİNİN
        // yazdığı serbest metindir ve uç kimliksizdir. (Uç ARTIK oran sınırlı — PublicMailSend,
        // IP başına 10/15dk; bu satır eskiden "oran sınırsız" diyordu ve o zaman DOĞRUYDU: küresel
        // sınırlayıcı yalnız Authorization başlığı taşıyanları bölümlüyor, bu yol "no limiter"a
        // düşüyordu. Sınır geldi ama aşağıdaki koruma KALDI: sınır isteği yavaşlatır, pahalı işi
        // ucuzlatmaz.)
        // Alan adı sahipliğinin ikinci adımı IIS'teki BÜTÜN siteleri gezer; onu her anonim POST'ta
        // çalıştırmak, tek satırlık bir istekle sunucuya iş yaptıran bir yükseltme kaldıracı olurdu.
        // `sent` doğruysa liste ve abone posta sunucusuna sorularak DOĞRULANMIŞTIR ve o yol ayrıca
        // 15 dakikalık bekleme süresiyle sınırlıdır — yani derin arama ancak gerçek bir liste için
        // ve seyrek çalışır. `sent` yanlışken ucuz adım (alan adı = site adı) yine denenir; sahibi
        // çözülemezse konu BOŞ kalır, tahmin edilmez.
        await auditService.WriteForSubscriptionAsync(
            "mail.list.unsubscribe_requested",
            SafeResource(list),
            $"Anonymous unsubscribe requested (confirmation sent: {sent}).",
            userId: null,
            await ListOwnerAsync(list, derinArama: sent, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Html(MailListUnsubscribePages.RequestAccepted());
    }

    /// <summary>
    /// İkinci adım (sayfa): onay düğmesini gösterir. DURUM DEĞİŞTİRMEZ — kasıtlı.
    /// </summary>
    /// <remarks>
    /// Posta istemcileri ve kurumsal güvenlik tarayıcıları iletideki bağlantıları ÖN-YÜKLER. Bu
    /// GET doğrudan çıkarma yapsaydı, abone bağlantıya hiç dokunmadan yalnızca iletiyi aldığı için
    /// listeden düşerdi. Çıkarma, yalnızca kullanıcının bastığı düğmenin POST'uyla olur.
    /// </remarks>
    [EnableRateLimiting(PanelRateLimits.PublicRead)]
    [HttpGet("unsubscribe/confirm")]
    public ContentResult ConfirmForm([FromQuery] string? token) =>
        Html(MailListUnsubscribePages.ConfirmForm(token ?? string.Empty, PathOf("unsubscribe/confirm")));

    /// <summary>
    /// İkinci adım (işlem): belirteci harcar ve aboneyi çıkarır.
    /// </summary>
    /// <remarks>
    /// Sonuç DÜRÜST bildirilir (istek adımının aksine). Sızıntı yoktur: belirteci elinde tutan kişi
    /// onu zaten o adrese gelen postadan almıştır ve belirteç 256 bit rastgeledir — deneyerek adres
    /// taranamaz. Süresi geçmiş bağlantıya "tamam" demek ise kullanıcıya çıkarıldığını sandırmak
    /// olurdu; oysa listede kalmaya devam ederdi.
    /// </remarks>
    [EnableRateLimiting(PanelRateLimits.PublicRead)]
    [RequestSizeLimit(PanelServerLimits.PublicFormMaxBytes)]
    [HttpPost("unsubscribe/confirm")]
    public async Task<ContentResult> ConfirmAsync([FromForm] string? token, CancellationToken cancellationToken)
    {
        var result = await unsubscribeService.ConfirmAsync(token ?? string.Empty, cancellationToken);

        // Burada derin arama KOŞULSUZ açık, çünkü liste adı istemciden GELMİYOR: panelin kendi
        // deposundan, harcanan belirtecin kaydından okunuyor. Belirteç 256 bit rastgeledir, yani
        // bu yolu tetiklemek için önce o adrese giden postaya erişmiş olmak gerekir. Geçersiz
        // belirteçte liste boş döner ve çözüm zaten ilk adımda durur.
        await auditService.WriteForSubscriptionAsync(
            "mail.list.unsubscribe_confirmed",
            SafeResource(result.List),
            $"Anonymous unsubscribe confirmation (removed: {result.Removed}).",
            userId: null,
            await ListOwnerAsync(result.List, derinArama: true, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Html(MailListUnsubscribePages.Result(result.Removed, result.List));
    }

    /// <summary>
    /// Dağıtım listesinin sahibi abonelik — liste ADRESİNİN alan adından ÖLÇÜLÜR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Liste adresi <c>duyuru@ornek.com</c> biçimindedir; sahiplik, panelin posta alan adı
    /// sahipliğiyle AYNI kuraldan (<see cref="AuditSubject.MailAddressOwnerAsync"/>) türetilir —
    /// burada ayrı bir kural yazmak, kiracının erişebildiği bir alan adındaki işlemin başka bir
    /// sahiple etiketlenmesi demek olurdu. Aynı alan adını iki abonelik sahipleniyorsa ya da
    /// sahiplik okunamıyorsa sonuç <c>null</c>'dır: yanlış kiracıya bağlamak, hiç bağlamamaktan
    /// kötüdür (komşu, başkasının kaydını görürdü).
    /// </para>
    /// <param name="derinArama">
    /// IIS bağlamalarına da bakılsın mı. <c>false</c> iken yalnız "alan adı = site adı" durumu
    /// çözülür; gerekçesi çağrı yerlerinde yazılı (kimliksiz uçta iş yükü kaldıracı).
    /// </param>
    /// </remarks>
    private Task<Guid?> ListOwnerAsync(string? listAddress, bool derinArama, CancellationToken cancellationToken) =>
        AuditSubject.MailAddressOwnerAsync(
            customerScopes,
            derinArama ? iisSiteService : null,
            listAddress,
            fallbackDomain: null,
            cancellationToken);

    private ContentResult Html(string html) => new()
    {
        Content = html,
        ContentType = HtmlContentType,
        StatusCode = StatusCodes.Status200OK
    };

    /// <summary>Form gönderim hedefi: göreli yol yeterli, ana ad tahmini gerektirmez.</summary>
    private static string PathOf(string action) => $"/api/v1/public/mail-lists/{action}";

    /// <summary>
    /// Onay e-postasına konacak MUTLAK adresin şablonu. Ana ad, panelin KENDİ yapılandırılmış
    /// adresinden gelir; <c>Host</c> başlığı yalnız adres hiç ayarlanmamışsa yedektir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN AÇIK — kapatıldı.</b> Adres eskiden doğrudan <c>Request.Host</c>'tan kuruluyordu.
    /// <c>appsettings.json</c>'da <c>"AllowedHosts": "*"</c> ve boru hattında
    /// <c>UseHostFiltering</c> YOK; yani <c>Host</c> başlığı tamamen İSTEMCİNİN yazdığı serbest
    /// metindi. Bu uç kimliksizdir ve GERÇEK bir aboneye PANELİN KENDİ alan adından e-posta
    /// GÖNDERTİR: saldırgan <c>Host: kotu.example</c> yazarak, kurbanın kutusuna panelin kendi
    /// itibarıyla ulaşan ve kendi sitesine götüren bir bağlantı koydurabiliyordu (kimlik avı +
    /// belirteç hırsızlığı: bağlantıya tıklayan abonenin tek kullanımlık belirteci saldırgana gider).
    /// </para>
    /// <para>
    /// <b>TEK KAYNAK:</b> panelin dışarıya verdiği adres zaten <see cref="PanelAddress"/>'te
    /// üretiliyor (parola sıfırlama, hoş geldin postası, tek kullanımlık giriş bağlantısı). Burada
    /// ikinci bir kural yazmak, birinin düzeltilip ötekinin açık kalması demekti.
    /// </para>
    /// <para>
    /// <b>Yedek neden KALDI (fazla kapatma sınırı):</b> <c>ServerAddress</c> kurulumda boş
    /// bırakılabiliyor. Yedeği kaldırmak, adresi ayarlamamış her kurulumda onay bağlantısını
    /// <c>&lt;sunucu-adresi&gt;</c> yer tutucusuyla ÖLÜ göndermek olurdu — yani özelliği kırardı.
    /// Adres ayarlıyken <c>Host</c> başlığı artık HİÇ kullanılmaz.
    /// </para>
    /// </remarks>
    private async Task<string> ConfirmUrlTemplateAsync(CancellationToken cancellationToken)
    {
        var preferences = await preferencesService.GetAsync(cancellationToken);
        var baseUrl = PanelAddress.BuildBaseUrl(
            preferences.ServerAddress,
            preferences.PreferredHttpsPort,
            fallbackHost: Request.Host.Value);

        return $"{baseUrl}{PathOf("unsubscribe/confirm")}?token={{token}}";
    }

    /// <summary>
    /// Denetim kaydına yazılacak kaynak adı. Gövde kimliksiz bir uçtan geldiği için SINIRLANIR:
    /// kayda sınırsız serbest metin yazdırmak, günlüğü şişirip okunmaz kılan bir kötüye kullanım
    /// yolu olurdu.
    /// </summary>
    private static string SafeResource(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "mail-list";
        }

        return trimmed.Length <= 320 ? trimmed : trimmed[..320];
    }
}
