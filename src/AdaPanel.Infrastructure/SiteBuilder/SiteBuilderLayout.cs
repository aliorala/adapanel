namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// Yayınlanan sitenin dosya düzeni. Kurucunun müşterinin klasöründe DOKUNDUĞU her yol burada.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek yerde:</b> "hangi dosyalar bizim" sorusunun cevabı üç yerde birden lazım —
/// yayın planı (neyi ezeceğiz), yayın (neyi yazacağız) ve oluşturucu (sayfa hangi adresten CSS
/// isteyecek). Üçü ayrı ayrı yazılsaydı biri değiştiğinde diğerleri sessizce eskirdi: sayfa
/// olmayan bir CSS'i isterdi ya da plan, yazacağımız bir dosyayı hiç saymadan "çakışma yok" derdi.
/// </para>
/// <para>
/// <b>Neden <c>assets/adapanel</c>:</b> varlıklar kendi alt klasörümüzde toplanır. Site kökünde
/// dağınık dosya bırakmak, müşterinin kendi <c>style.css</c>'ini ezme ihtimalini bedava üretirdi.
/// Kök dizine yazdığımız tek dosya <c>index.html</c>'dir ve o zaten sayfanın kendisidir.
/// </para>
/// </remarks>
public static class SiteBuilderLayout
{
    public const string IndexFile = "index.html";

    public const string AssetFolder = "assets/adapanel";

    public const string StyleFile = AssetFolder + "/site.css";

    public const string ImageFolder = AssetFolder + "/img";

    /// <summary>Sayfadaki <c>src</c> önekinin tamamı (site köküne göreli).</summary>
    public const string ImageFolderUrl = ImageFolder + "/";

    /// <summary>
    /// Yayında ASLA dokunulmayan yollar.
    /// </summary>
    /// <remarks>
    /// <c>.well-known</c> burada tesadüfen değil: Plesk/Sitejet tarafında yayın belge kökünü
    /// temizlerken bu klasörü de sildiği için sertifika yenilemesi (HTTP-01 doğrulaması) bozuluyordu.
    /// Bizim yayınımız zaten hiçbir şey SİLMİYOR, ama listeyi açıkça yazmak kararı görünür kılar:
    /// bu yollar bir gün "temizlik" eklenmek istenirse de kapsam dışıdır.
    /// </remarks>
    public static readonly IReadOnlyList<string> NeverTouch =
    [
        ".well-known",
        "web.config",
        "app_data"
    ];

    /// <summary>Yayında yazılacak dosyaların göreli yolları (görseller hariç; onlar tasarıma bağlı).</summary>
    public static readonly IReadOnlyList<string> CoreFiles = [IndexFile, StyleFile];

    /// <summary>Göreli yolu Windows dosya yoluna çevirir (URL biçiminden dosya sistemi biçimine).</summary>
    public static string ToFileSystemPath(string relativeUrlPath) =>
        relativeUrlPath.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Verilen göreli yol dokunulmaz listesindeki bir şeye mi giriyor.</summary>
    public static bool IsProtected(string relativeUrlPath)
    {
        var first = relativeUrlPath.Replace('\\', '/').TrimStart('/').Split('/')[0];
        return NeverTouch.Any(item => string.Equals(item, first, StringComparison.OrdinalIgnoreCase));
    }
}
