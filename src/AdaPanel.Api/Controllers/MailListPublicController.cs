using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Mail;
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
/// <b>Yüzey KASITEN dar tutuldu.</b> Bu denetleyici YALNIZCA şunu yapar: bir adresi bir listeden
/// çıkarır. Liste listelemez, abone göstermez, "bu adres abone mi" sorusunu CEVAPLAMAZ. İstek
/// adımı, adres abone olsun ya da olmasın AYNI cevabı döner — aksi hâlde uç, bir alan adının
/// hangi adreslerinin listede olduğunu taramaya yarayan bir E-POSTA HASAT aracına dönerdi.
/// </para>
/// <para>
/// <b>Antiforgery:</b> <see cref="IgnoreAntiforgeryTokenAttribute"/> gereklidir çünkü istek panelin
/// SPA'sinden değil, e-postadaki bir bağlantıdan/genel bir formdan gelir ve CSRF çerezi taşımaz.
/// CSRF'in koruduğu şey burada zaten yok: uç kimlik doğrulamaz, dolayısıyla "kurbanın oturumunu
/// kullanmak" diye bir şey mümkün değildir. Yapılabilecek en kötü şey, saldırganın KENDİ
/// tarayıcısından bu ucu çağırmasıdır — ki zaten herkese açık.
/// </para>
/// <para>
/// <b>Gizli giriş yolu:</b> <c>SecureEntryPathMiddleware</c> içinde DAR bir muafiyet var
/// (<c>IsMailListUnsubscribePath</c>); posta istemcisi otomatik yapılandırma yollarıyla aynı
/// gerekçe: abone, panel yöneticisinin izin listesindeki bir IP'den değil, kendi rastgele
/// adresinden gelir ve özel bir başlık gönderemez.
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
    /// <summary>
    /// Birinci adım: onay postası ister. Cevap, adres abone olsun ya da olmasın AYNIDIR.
    /// </summary>
    [HttpPost("unsubscribe/request")]
    public async Task<ActionResult<ApiResponse<object?>>> RequestAsync(
        [FromBody] MailListUnsubscribeRequest request,
        CancellationToken cancellationToken)
    {
        var confirmUrl = BuildConfirmUrlTemplate();
        var sent = false;
        try
        {
            sent = await unsubscribeService.RequestAsync(request.List, request.Email, confirmUrl, cancellationToken);
        }
        catch (Exception exception)
        {
            // HATA DA SIZDIRILMAZ. Posta gönderilemedi ya da liste yok — ikisi de aynı cevabı verir,
            // yoksa saldırgan hata farkından adresin/listenin varlığını çıkarabilirdi.
            logger.LogWarning(exception, "Abonelikten çıkma isteği işlenemedi.");
        }

        // Denetim kaydı: kimliksiz bir uçtan gelen HER istek yazılır (userId yok, IP var). İstenen
        // ADRES kayda GİRMEZ — kaydın kendisi, korumaya çalıştığımız adres listesinin ikinci bir
        // kopyası olmamalı. Yazılan şey: hangi listeye, hangi IP'den, posta gitti mi.
        await auditService.WriteAsync(
            "mail.list.unsubscribe_requested",
            SafeResource(request.List),
            $"Anonymous unsubscribe requested (confirmation sent: {sent}).",
            userId: null,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        // TEK TİP CEVAP.
        return Ok(ApiResponse<object?>.Ok(null, "mail.unsubscribe_requested"));
    }

    /// <summary>
    /// İkinci adım: belirteci harcar ve aboneyi çıkarır.
    /// </summary>
    /// <remarks>
    /// Burada sonuç DÜRÜST bildirilir (istek adımının aksine). Sızıntı yoktur: belirteci elinde
    /// tutan kişi onu zaten o adrese gelen postadan almıştır ve belirteç 256 bit rastgeledir —
    /// deneyerek adres taranamaz. Süresi geçmiş bağlantıya "tamam" demek ise kullanıcıya
    /// çıkarıldığını sandırmak olurdu; oysa listede kalmaya devam ederdi.
    /// </remarks>
    [HttpPost("unsubscribe/confirm")]
    public async Task<ActionResult<ApiResponse<MailListUnsubscribeResultDto>>> ConfirmAsync(
        [FromBody] MailListUnsubscribeConfirmRequest request,
        CancellationToken cancellationToken)
    {
        var result = await unsubscribeService.ConfirmAsync(request.Token, cancellationToken);

        await auditService.WriteAsync(
            "mail.list.unsubscribe_confirmed",
            SafeResource(result.List),
            $"Anonymous unsubscribe confirmation (removed: {result.Removed}).",
            userId: null,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<MailListUnsubscribeResultDto>.Ok(result, result.Message));
    }

    /// <summary>
    /// Onay adresinin şablonu: isteğin geldiği ana ad üzerinden kurulur.
    /// </summary>
    /// <remarks>
    /// Sabit bir ana ad yazılamaz — panel her kurulumda başka bir adla yayınlanır. İstekteki
    /// <c>Host</c> kullanılır; bağlantı zaten yalnız o adrese gönderilen e-postaya konur.
    /// </remarks>
    private string BuildConfirmUrlTemplate() =>
        $"{Request.Scheme}://{Request.Host}/api/v1/public/mail-lists/unsubscribe/confirm?token={{token}}";

    /// <summary>
    /// Denetim kaydına yazılacak kaynak adı. İstek gövdesi kimliksiz bir uçtan geldiği için
    /// SINIRLANIR: kayıt satırına sınırsız uzunlukta serbest metin yazdırmak, günlüğü şişirmeye
    /// ve okunmaz kılmaya yarayan bir kötüye kullanım yolu olurdu.
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
