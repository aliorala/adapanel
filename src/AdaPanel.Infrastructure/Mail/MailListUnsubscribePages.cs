using System.Text.Encodings.Web;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Abonelikten çıkma akışının HTML sayfaları.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden panelin SPA'si değil de sunucu tarafı HTML:</b> panelin arayüzü GİZLİ GİRİŞ YOLUNUN
/// arkasında sunulur; listeden çıkmak isteyen abone o yolu bilmez ve bilmemelidir. Ayrıca abone
/// panelin kullanıcısı değildir. Bu yüzden akışın kendi kendine yeten, bağımsız sayfaları var.
/// </para>
/// <para>
/// <b>Tüm kullanıcı verisi KAÇIRILARAK basılır.</b> Liste adresi sorgu dizesinden gelir ve sayfaya
/// yazılır; kaçırılmasaydı kimliksiz bir uçta yansıtılmış XSS olurdu.
/// </para>
/// </remarks>
public static class MailListUnsubscribePages
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
        .addr { font-weight: 600; word-break: break-all; }
        input[type=email] { width: 100%; padding: 10px; font-size: 1rem; border: 1px solid #c9ced6;
               border-radius: 8px; box-sizing: border-box; }
        button { margin-top: 14px; padding: 10px 18px; font-size: 1rem; border: 0; border-radius: 8px;
               background: #2f6feb; color: #fff; cursor: pointer; }
        .muted { color: #5b6472; font-size: .9rem; }
        </style>
        </head>
        <body><main>{{body}}</main></body>
        </html>
        """;

    /// <summary>Birinci adım: abone kendi adresini yazar.</summary>
    public static string RequestForm(string listAddress, string postUrl) => Page(
        "Abonelikten cikma / Unsubscribe",
        $"""
        <h1>Abonelikten çık / Unsubscribe</h1>
        <p>Liste / List: <span class="addr">{Encode(listAddress)}</span></p>
        <p>Listeden çıkarılmak istediğiniz e-posta adresini yazın. Adrese bir onay bağlantısı göndereceğiz.<br>
        <span class="muted">Enter the email address to remove. We will send a confirmation link to it.</span></p>
        <form method="post" action="{Encode(postUrl)}">
          <input type="hidden" name="list" value="{Encode(listAddress)}">
          <input type="email" name="email" required placeholder="ornek@alanadi.com" autocomplete="email">
          <button type="submit">Onay bağlantısı gönder / Send confirmation</button>
        </form>
        """);

    /// <summary>
    /// Birinci adımın cevabı. Adres abone OLSA DA OLMASA DA aynıdır — aksi hâlde sayfa, bir alan
    /// adının hangi adreslerinin listede olduğunu taramaya yarayan bir e-posta hasat aracı olurdu.
    /// </summary>
    public static string RequestAccepted() => Page(
        "Istek alindi / Request received",
        """
        <h1>İstek alındı / Request received</h1>
        <p>Adres bu listeye aboneyse, çıkma bağlantısını içeren bir onay e-postası gönderildi.
        Lütfen gelen kutunuzu kontrol edin.</p>
        <p class="muted">If the address is subscribed to this list, a confirmation email with an
        unsubscribe link has been sent. Please check your inbox.</p>
        """);

    /// <summary>
    /// İkinci adım: onay DÜĞMESİ.
    /// </summary>
    /// <remarks>
    /// <b>Neden bağlantıya tıklamak doğrudan çıkarmıyor:</b> posta istemcileri ve kurumsal güvenlik
    /// tarayıcıları iletideki bağlantıları ÖN-YÜKLER. GET isteği doğrudan çıkarsaydı, abone
    /// bağlantıya hiç dokunmadan, yalnızca iletiyi aldığı için listeden düşerdi. Bu yüzden GET
    /// yalnız bu sayfayı gösterir; çıkarma, kullanıcının bastığı düğmenin POST'uyla olur.
    /// </remarks>
    public static string ConfirmForm(string token, string postUrl) => Page(
        "Abonelikten cikmayi onayla / Confirm unsubscribe",
        $"""
        <h1>Abonelikten çıkmayı onaylayın</h1>
        <p>Onaylarsanız bu listeye gönderilen postaları artık almayacaksınız. Posta kutunuz silinmez.<br>
        <span class="muted">If you confirm, you will stop receiving mail sent to this list.
        Your mailbox is not deleted.</span></p>
        <form method="post" action="{Encode(postUrl)}">
          <input type="hidden" name="token" value="{Encode(token)}">
          <button type="submit">Onayla / Confirm</button>
        </form>
        """);

    /// <summary>Sonuç sayfası. Başarısızlık DÜRÜSTÇE söylenir; "tamam" deyip çıkarmamak olmaz.</summary>
    public static string Result(bool removed, string listAddress) => removed
        ? Page(
            "Listeden cikarildiniz / Unsubscribed",
            $"""
            <h1>Listeden çıkarıldınız</h1>
            <p>Artık <span class="addr">{Encode(listAddress)}</span> listesine gönderilen postaları almayacaksınız.</p>
            <p class="muted">You have been removed from {Encode(listAddress)}.</p>
            """)
        : Page(
            "Baglanti gecersiz / Invalid link",
            """
            <h1>Bağlantı geçersiz ya da süresi dolmuş</h1>
            <p><strong>Listeden çıkarılmadınız.</strong> Lütfen yeni bir çıkma isteği oluşturun.</p>
            <p class="muted">The link is invalid or has expired. You were NOT removed;
            please start a new unsubscribe request.</p>
            """);
}
