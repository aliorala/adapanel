namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Bir HTTP durum kodu için sitenin kendi hata belgesi (IIS <c>httpErrors/error</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden yalnız site-göreli yol:</b> IIS'in <c>responseMode</c> seçenekleri arasında
/// <c>File</c> (MUTLAK disk yolu) ve <c>Redirect</c> (dış URL) de var. Çok kiracılı panelde
/// <c>File</c> kabul etmek, kiracıya "şu dosyayı hata sayfası olarak yayınla" diyebilme yetkisi
/// verirdi — <c>C:\Windows\...</c> ya da KOMŞU SİTENİN dosyası da geçerli bir yol olurdu.
/// Bu yüzden panel yalnız site-göreli yol kabul eder ve <c>ExecuteURL</c> yazar: URL aynı
/// uygulama içinde çalıştırılır, tanım gereği siteden dışarı çıkamaz.
/// </para>
/// </remarks>
public sealed class SiteErrorPageDto
{
    /// <summary>HTTP durum kodu (IIS şeması 400-999 aralığını kabul eder).</summary>
    public int StatusCode { get; set; }

    /// <summary>Site köküne göre yol: <c>/hatalar/404.html</c>.</summary>
    public string Path { get; set; } = string.Empty;
}
