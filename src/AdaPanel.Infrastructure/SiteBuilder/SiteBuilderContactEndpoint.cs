using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// İletişim formunun KİMLİKSİZ ucunun adresi ve site anahtarının doğrulaması.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden yol burada, tek yerde:</b> aynı dizge üç yerde birden lazım — üretilen sayfanın
/// <c>action</c> özniteliği, denetleyicinin rota özniteliği ve gizli giriş yolu ara katmanının
/// muafiyet satırı. Üçü ayrı yazılsaydı, biri değiştiğinde form sessizce 404 dönerdi ve bunu
/// yalnız ziyaretçi görürdü — yani hiç kimse.
/// </para>
/// <para>
/// <b>Neden adreste site ADI değil ANAHTARI var:</b> anahtar
/// (<c>SiteBuilderService.StorageKey</c>) hem dosya sistemi için güvenli hem de sitenin IIS
/// adını dışarı vermez. Ayrıca yayınlanmış bir sayfadaki adresi gören biri, o adresten başka bir
/// sitenin anahtarını TÜRETEMEZ: anahtarın sonundaki sekiz haneli özet, tahmin edilerek başka
/// siteye geçmeyi engeller. Site adı doğrudan yazılsaydı, uç "adı bilinen her siteye form
/// gönderimi dene" biçiminde taranabilir olurdu.
/// </para>
/// </remarks>
public static partial class SiteBuilderContactEndpoint
{
    /// <summary>Ucun kök yolu. Gizli giriş yolu muafiyeti TAM olarak bu segmente verilir.</summary>
    public const string RoutePrefix = "/api/v1/public/site-forms";

    /// <summary>
    /// Site anahtarının kabul edilen biçimi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>StorageKey</c> ya sadece sekiz haneli özeti ya da <c>ad-ÖZET</c> biçimini üretir. Kalıp
    /// bunu ölçer: yol ayırıcısı, iki nokta ve <c>..</c> kalıba UYMAZ, yani anahtar bir dosya
    /// yoluna çevrilmeden önce kaçış ihtimali kapanır. (<c>JsonFileStore.ResolvePath</c> ikinci
    /// savunma hattı olarak yerinde duruyor; bu birincisi.)
    /// </para>
    /// <para>
    /// Ad kısmındaki karakter kümesi <c>StorageKey</c>'in üretebileceğinden DAR: yalnız harf,
    /// rakam, alt çizgi ve tire. Daha egzotik adlı bir sitede form çalışmaz — bunu bilerek kabul
    /// ediyoruz, çünkü kimliksiz bir uçta "geniş kabul et" tarafında hata yapmanın bedeli, "dar
    /// kabul et" tarafında hata yapmanın bedelinden büyüktür. Dar taraftaki hata görünürdür
    /// (form çalışmaz ve kullanıcıya söylenir); geniş taraftaki hata sessizdir.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"^(?:[\p{L}\p{Nd}_\-]{1,48}-)?[0-9A-F]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex SiteKeyPattern();

    public static bool IsValidSiteKey(string? siteKey) =>
        !string.IsNullOrWhiteSpace(siteKey) && SiteKeyPattern().IsMatch(siteKey.Trim());

    /// <summary>
    /// Formun gönderim hedefi. Ana ad boşsa ya da anahtar geçersizse BOŞ döner (form basılmaz).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ana ad panelin erişim ayarından okunur, isteğin <c>Host</c> başlığından DEĞİL.</b>
    /// Yayın isteğini yapan yönetici panele <c>localhost:8890</c> üzerinden bağlanmış olabilir;
    /// o adresi müşterinin sitesine gömseydik, form yalnız yöneticinin kendi makinesinde çalışan
    /// bir "form" olurdu ve ziyaretçi bunu asla anlayamazdı.
    /// </para>
    /// <para>
    /// <b>Yalnız <c>https</c>:</b> form ziyaretçinin adını, adresini ve mesajını taşıyor. Panelin
    /// erişim ayarı zaten bir sertifikayla kurulur; <c>http</c>'ye düşmek, kullanıcıya sorulmadan
    /// alınmış bir gizlilik kararı olurdu.
    /// </para>
    /// </remarks>
    public static string BuildAction(string? panelHostName, string siteKey)
    {
        var host = (panelHostName ?? string.Empty).Trim().Trim('/').ToLowerInvariant();
        if (host.Length == 0 || !IsValidSiteKey(siteKey) || !HostPattern().IsMatch(host))
        {
            return string.Empty;
        }

        // Anahtar ASCII DIŞI harf içerebilir (Türkçe site adları). Yüzde kodlaması yapılmasaydı
        // adres, tarayıcıya ve sunucuya göre farklı kodlanır ve rota bazen eşleşmezdi.
        return $"https://{host}{RoutePrefix}/{Uri.EscapeDataString(siteKey.Trim())}/contact";
    }

    /// <summary>
    /// Ana adın biçimi. <c>PanelPublicAccessService</c> zaten katı bir kalıptan geçiriyor; burada
    /// tekrar ölçülmesi, değerin dosyadan (elle düzenlenebilir bir JSON'dan) okunmasından.
    /// </summary>
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
}
