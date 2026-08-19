namespace AdaPanel.Core.Dtos.Seo;

/// <summary>
/// Taramanın bulduğu TEK bir teknik SEO sorunu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden çeviri anahtarı, neden hazır cümle değil:</b> panel iki dilli. Sunucu "Sayfada başlık
/// etiketi yok" cümlesini üretseydi, İngilizce oturumdaki kullanıcı Türkçe bulgu görürdü. Bu yüzden
/// sunucu yalnız ANAHTAR + ÖLÇÜLEN DEĞER üretir, cümleyi arayüz kurar (kalıp: SiteHealthCheckDto).
/// </para>
/// <para>
/// <b><see cref="Detail"/> neden dile bağımsız:</b> içinde ölçülen ham veri durur (başlığın kendi
/// metni, HTTP kodu, süre). Çevrilmesi gereken bir şey değildir ve çevrilseydi kullanıcı asıl
/// değeri hiç göremezdi.
/// </para>
/// </remarks>
public sealed class SeoFindingDto
{
    /// <summary>Çeviri anahtarının son parçası (ör. <c>missing_title</c>).</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary><c>error</c> | <c>warn</c> | <c>info</c>.</summary>
    public string Severity { get; init; } = "warn";

    /// <summary>Sorunun görüldüğü sayfa yolu (<c>/</c>, <c>/urunler</c>). Site geneli bulgularda boş.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Ölçülen ham değer — dile bağımsız (başlık metni, HTTP kodu, süre, sayı).</summary>
    public string Detail { get; init; } = string.Empty;
}
