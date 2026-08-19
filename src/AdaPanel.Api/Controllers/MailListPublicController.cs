using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
/// </remarks>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/v1/public/mail-lists")]
public sealed class MailListPublicController(
    MailListUnsubscribeService unsubscribeService,
    IAuditService auditService,
    ILogger<MailListPublicController> logger) : ControllerBase
{
    private const string HtmlContentType = "text/html; charset=utf-8";

    /// <summary>Birinci adım (sayfa): abone kendi adresini yazar. DURUM DEĞİŞTİRMEZ.</summary>
    [HttpGet("unsubscribe")]
    public ContentResult RequestForm([FromQuery] string? list) =>
        Html(MailListUnsubscribePages.RequestForm(list ?? string.Empty, PathOf("unsubscribe")));

    /// <summary>
    /// Birinci adım (işlem): onay postası ister. Cevap, adres abone olsun ya da olmasın AYNIDIR.
    /// </summary>
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
                AbsoluteUrl("unsubscribe/confirm") + "?token={token}",
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
        await auditService.WriteAsync(
            "mail.list.unsubscribe_requested",
            SafeResource(list),
            $"Anonymous unsubscribe requested (confirmation sent: {sent}).",
            userId: null,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
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
    [HttpPost("unsubscribe/confirm")]
    public async Task<ContentResult> ConfirmAsync([FromForm] string? token, CancellationToken cancellationToken)
    {
        var result = await unsubscribeService.ConfirmAsync(token ?? string.Empty, cancellationToken);

        await auditService.WriteAsync(
            "mail.list.unsubscribe_confirmed",
            SafeResource(result.List),
            $"Anonymous unsubscribe confirmation (removed: {result.Removed}).",
            userId: null,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Html(MailListUnsubscribePages.Result(result.Removed, result.List));
    }

    private ContentResult Html(string html) => new()
    {
        Content = html,
        ContentType = HtmlContentType,
        StatusCode = StatusCodes.Status200OK
    };

    /// <summary>Form gönderim hedefi: göreli yol yeterli, ana ad tahmini gerektirmez.</summary>
    private static string PathOf(string action) => $"/api/v1/public/mail-lists/{action}";

    /// <summary>
    /// E-postaya konacak MUTLAK adres: isteğin geldiği ana ad üzerinden kurulur.
    /// </summary>
    /// <remarks>
    /// Sabit bir ana ad yazılamaz — panel her kurulumda başka bir adla yayınlanır. İstekteki
    /// <c>Host</c> kullanılır; bağlantı zaten yalnız o adrese gönderilen e-postaya konur.
    /// </remarks>
    private string AbsoluteUrl(string action) => $"{Request.Scheme}://{Request.Host}{PathOf(action)}";

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
