using System.Text.Encodings.Web;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// İletişim formunun ziyaretçiye dönen cevap sayfaları.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden sunucu tarafı HTML:</b> formu dolduran kişi panelin kullanıcısı DEĞİLDİR ve panelin
/// gizli giriş yolunu bilmez/bilmemelidir. Cevap, panelin SPA'si değil kendi kendine yeten bir
/// sayfadır — abonelikten çıkma akışıyla aynı gerekçe.
/// </para>
/// <para>
/// <b>Her sonuç AYRI bir sayfa ve sonuç DÜRÜST söylenir.</b> Bu özelliğin ilk turda kapsam dışı
/// bırakılma sebebi tam olarak buydu: "gönderildi" deyip yutan bir form, ziyaretçinin yanıt
/// beklemesine yol açar ve hiç form olmamasından kötüdür. Gönderim başarısızsa sayfa bunu söyler
/// ve ziyaretçiye DOĞRUDAN iletişim yolunu (sayfadaki e-posta/telefon) hatırlatır.
/// </para>
/// <para>
/// <b>Ziyaretçinin yazdığı hiçbir metin bu sayfalarda YANSITILMAZ.</b> Yansıtılsaydı kimliksiz bir
/// uçta yansıtılmış XSS yüzeyi doğardı; kaçırma doğru yapılsa bile o yüzeyi hiç açmamak daha
/// dayanıklı. Ziyaretçi zaten ne yazdığını biliyor — geri okuması gereken bir şey yok.
/// </para>
/// <para>
/// <c>noindex</c>: bu sayfalar panelin ana adında açılır; arama motorlarına düşmelerinin hiçbir
/// faydası yok, panelin varlığını duyurmaları ise gereksiz bir bilgi sızıntısıdır.
/// </para>
/// </remarks>
public static class SiteBuilderContactPages
{
    private static string Encode(string? value) => HtmlEncoder.Default.Encode(value ?? string.Empty);

    // $$ ile yazıldı: CSS'in süslü parantezleri DÜZ METİNDİR, interpolasyon {{...}} ile yapılır.
    private static string Page(string title, string body) =>
        $$"""
        <!doctype html>
        <html lang="tr">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex, nofollow">
        <title>{{Encode(title)}}</title>
        <style>
        body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; background: #f5f6f8;
               color: #1c1f23; margin: 0; padding: 32px 16px; }
        main { max-width: 520px; margin: 0 auto; background: #fff; border-radius: 12px;
               padding: 28px; box-shadow: 0 1px 3px rgba(0,0,0,.08); }
        h1 { font-size: 1.25rem; margin: 0 0 12px; }
        p { line-height: 1.55; margin: 0 0 14px; }
        .muted { color: #5b6472; font-size: .9rem; }
        .bad { color: #a3261f; font-weight: 600; }
        </style>
        </head>
        <body><main>{{body}}</main></body>
        </html>
        """;

    /// <summary>Sonucu sayfaya çeviren TEK yer.</summary>
    /// <remarks>
    /// Tek giriş noktası olması bilinçli: yeni bir sonuç değeri eklendiğinde derleyici burada
    /// eksik dalı göstermez, ama <c>default</c> dalı "gönderilemedi" der. Yani bilinmeyen bir
    /// durumun varsayılan anlatımı "başarılı" değil, "başarısız" olur — ziyaretçiye yanlışlıkla
    /// "gönderildi" demenin yapısal olarak imkânsız kılınması.
    /// </remarks>
    public static string For(SiteBuilderContactOutcome outcome) => outcome switch
    {
        SiteBuilderContactOutcome.Sent => Sent(),

        // TUZAĞA DÜŞEN İSTEK "gönderildi" sayfasını görür ve posta GÖNDERİLMEZ.
        //
        // Bu, "sonucu dürüst söyle" kuralının bilinçli tek istisnası ve gerekçesi ölçülebilir:
        // kuralın koruduğu şey İNSANIN yanlış bilgilenmesi. Bal küpü alanı ekranda görünmez,
        // klavye sırasında değildir, otomatik doldurma kapalıdır ve etiketi "bu alanı boş bırakın"
        // der — yani ekran okuyucu kullanan bir ziyaretçi bile onu yanlışlıkla dolduramaz.
        // Doldurana "tuzağa düştün" demek ise tuzağı bir ÖĞRETİCİYE çevirir: bot alanı atlamayı
        // öğrenir ve koruma bir kez kullanılıp biter.
        SiteBuilderContactOutcome.Trapped => Sent(),

        SiteBuilderContactOutcome.Invalid => Page(
            "Gönderilemedi / Not sent",
            """
            <h1 class="bad">Mesaj gönderilemedi</h1>
            <p>Formdaki alanlar eksik ya da beklenen biçimde değil. Adınızı, geçerli bir e-posta
            adresini ve mesajınızı yazıp yeniden deneyin.</p>
            <p class="muted">The message was <strong>not</strong> sent: a field is missing or malformed.
            Please go back, complete the form and try again.</p>
            """),

        SiteBuilderContactOutcome.RateLimited => Page(
            "Gönderilemedi / Not sent",
            """
            <h1 class="bad">Mesaj gönderilemedi</h1>
            <p>Kısa süre içinde çok fazla gönderim yapıldı. Lütfen birkaç dakika sonra yeniden deneyin;
            aceleniz varsa sayfadaki e-posta adresini ya da telefonu doğrudan kullanabilirsiniz.</p>
            <p class="muted">The message was <strong>not</strong> sent: too many submissions in a short
            time. Please try again in a few minutes, or use the address/phone shown on the page.</p>
            """),

        SiteBuilderContactOutcome.NotAvailable => Page(
            "Form kullanılamıyor / Form unavailable",
            """
            <h1 class="bad">Bu form şu anda kullanılamıyor</h1>
            <p>Mesajınız <strong>gönderilmedi</strong>. Site sahibine ulaşmak için sayfadaki e-posta
            adresini ya da telefonu kullanın.</p>
            <p class="muted">This form is not available and your message was <strong>not</strong> sent.
            Please use the e-mail address or phone number shown on the page.</p>
            """),

        _ => Page(
            "Gönderilemedi / Not sent",
            """
            <h1 class="bad">Mesaj gönderilemedi</h1>
            <p>Sunucu şu anda e-posta gönderemedi, bu yüzden mesajınız <strong>iletilmedi</strong>.
            Lütfen sayfadaki e-posta adresini ya da telefonu doğrudan kullanın; site sahibine
            durum kaydedildi.</p>
            <p class="muted">The server could not send e-mail, so your message was <strong>not</strong>
            delivered. Please contact the site owner directly using the address or phone on the page.</p>
            """)
    };

    private static string Sent() => Page(
        "Mesajınız gönderildi / Message sent",
        """
        <h1>Mesajınız gönderildi</h1>
        <p>Site sahibine e-posta olarak iletildi. Yanıt, formda yazdığınız adrese gelecektir.</p>
        <p class="muted">Your message has been delivered to the site owner by e-mail. Any reply will be
        sent to the address you entered.</p>
        """);
}
