namespace AdaPanel.Core.Dtos.Settings;

/// <summary>
/// Panelin GÖRÜNEN markası. İki yerde kullanılır:
/// <list type="bullet">
/// <item>ETKİN marka — istekte bulunan kişinin göreceği marka (giriş ekranı + panel kabuğu).</item>
/// <item>HAM marka — düzenleme formunun kendi kapsamında sakladığı değerler.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Görseller neden JSON'un İÇİNDE (data URI) taşınıyor, ayrı bir dosya ucundan değil:</b>
/// tarayıcı bir <c>&lt;img src="/api/v1/..."&gt;</c> isteğine özel başlık EKLEYEMEZ, panelin
/// <c>SecureEntryPathMiddleware</c>'i ise <c>/api</c> altındaki her isteğe
/// <c>X-AdaPanel-Entry-Path</c> başlığını zorunlu kılar. Yani ayrı bir görsel ucu yazılsaydı
/// logo HER SEFERİNDE 404 dönerdi — özellik var görünüp hiç çalışmazdı (panelde bu hata sınıfı
/// daha önce phpMyAdmin vekilinde yaşandı). Data URI ayrıca ikinci bir ikili uç açmadığı için
/// içerik-tipi karıştırma ve kapsam sayımı (hangi bayilerin logosu var) yüzeyini de kapatır.
/// Bedeli ölçülü: base64 %33 şişirir, bu yüzden yükleme sınırları dar tutulur
/// (bkz. BrandingImageRules).
/// </para>
/// <para>
/// <b>Boş alan = "yok"</b> demektir; istemci o alan için panel varsayılanına düşer. <c>null</c>
/// kullanılmaz: arayüzde her alan bir dizgeye bağlanıyor ve <c>null</c> "undefined" olarak basılırdı.
/// </para>
/// </remarks>
public sealed class BrandingDto
{
    /// <summary>Panel adı (başlık çubuğu + kenar menü). Boşsa istemci ürün adını kullanır.</summary>
    public string PanelName { get; set; } = string.Empty;

    /// <summary>Vurgu rengi <c>#rrggbb</c>; boş = panelin kendi rengi. Doğrulaması BrandingTextRules'te.</summary>
    public string AccentColor { get; set; } = string.Empty;

    /// <summary>Logo, <c>data:image/png;base64,…</c> biçiminde. Boş = logo yok.</summary>
    public string LogoDataUrl { get; set; } = string.Empty;

    /// <summary>Favicon (tarayıcı sekmesi), data URI. Boş = ürün favicon'u.</summary>
    public string FaviconDataUrl { get; set; } = string.Empty;

    /// <summary>Logoya tıklanınca gidilecek adres (yalnız http/https). Boş = tıklanamaz.</summary>
    public string LogoLinkUrl { get; set; } = string.Empty;

    /// <summary>
    /// Bu markanın KAYNAĞI: <c>server</c> (sunucu geneli, yönetici belirler) ya da <c>reseller</c>
    /// (kullanıcıyı barındıran bayi). Arayüz bunu göstermek için değil, düzenleme formunun kimin
    /// adına kaydettiğini KULLANICIYA yazmak için kullanır.
    /// </summary>
    public string Scope { get; set; } = "server";
}
