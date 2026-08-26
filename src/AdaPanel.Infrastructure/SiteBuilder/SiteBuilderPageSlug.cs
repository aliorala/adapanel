using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// Sayfa adresinin (slug) TEK doğrulama yeri.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir sınıf:</b> bu dizge tek bir yere değil, ÜÇ ayrı tehlikeli hedefe birden
/// giriyor:
/// </para>
/// <list type="number">
/// <item>Müşterinin sunucusunda bir KLASÖR ADI olur (<c>{slug}\index.html</c>).</item>
/// <item>Üretilen sayfada bir <c>href</c> ÖZNİTELİĞİ olur.</item>
/// <item>Yayın planındaki "yazılacak dosyalar" ve "öksüz dosyalar" listelerinde bir YOL olur.</item>
/// </list>
/// <para>
/// Üç kural ayrı ayrı yazılsaydı biri geride kalırdı ve o tek yer ya yol kaçışı (<c>../</c>) ya
/// da öznitelikten çıkma (<c>" onerror=</c>) demek olurdu. Kalıp burada, kapalı bir ALLOWLIST
/// olarak duruyor: kabul edilen karakter kümesi sayılıdır, reddedilen küme değil.
/// </para>
/// <para>
/// <b>Neden "düzelt" değil "reddet":</b> geçersiz bir slug'ı temizleyip kullanmak, kullanıcının
/// yazmadığı bir adrese dosya yazmak olurdu — üstelik bir sonraki kaydetmede başka bir adrese
/// düşebilir ve her seferinde diskte yeni bir öksüz sayfa bırakırdı. Geçersiz sayfa DÜŞÜRÜLÜR.
/// </para>
/// </remarks>
public static partial class SiteBuilderPageSlug
{
    /// <summary>Bir sitede izin verilen en fazla EK sayfa sayısı.</summary>
    /// <remarks>
    /// Sınırsız sayfa = sınırsız büyüyen bir JSON ve tek "Yayınla" tıklamasında sınırsız dosya
    /// yazımı. Üst sınır, teknik olmayan bir kullanıcının kuracağı tanıtım sitesi için fazlasıyla
    /// geniş; sınırsız bırakmak ise panelin kendi diskine karşı bir kaldıraç olurdu.
    /// </remarks>
    public const int MaxPages = 12;

    /// <summary>
    /// Kabul edilen biçim: küçük harf/rakamla başlar ve biter, arada tire olabilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Büyük harf YOK: Windows dosya sistemi büyük/küçük harfe duyarsız, ama üretilen
    /// <c>href</c>'i çözen sunucu ve tarayıcı önbelleği duyarlı davranabilir. İki ayrı sayfa gibi
    /// görünen tek bir dosya, kullanıcının anlayamayacağı bir arızadır — kaynağında kapatıldı.
    /// </para>
    /// <para>
    /// Nokta YOK: <c>..</c> ve <c>web.config</c> gibi adların hepsi noktayla kurulur. Noktayı
    /// kalıptan çıkarmak, bu sınıfın tamamını tek bir kuralla güvenli kılar.
    /// </para>
    /// <para>
    /// Yüzde işareti YOK: <c>%2e%2e</c> gibi kodlanmış kaçışlar da bu kalıba uymaz.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,46}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    /// <summary>
    /// Slug olarak kullanılamayacak adlar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>assets</c> kurucunun KENDİ varlık klasörünün ilk segmentidir
    /// (<see cref="SiteBuilderLayout.AssetFolder"/>); bir sayfa oraya yerleşseydi
    /// <c>assets/index.html</c> ile <c>assets/adapanel/site.css</c> aynı ağacı paylaşır ve
    /// "hangi dosya bizim" sorusu bulanıklaşırdı.
    /// </para>
    /// <para>
    /// <c>app_data</c>, <c>app_code</c>, <c>bin</c>: ASP.NET/IIS'in ÖZEL klasörleridir; IIS bu
    /// yolları sunmayı reddeder, yani oraya yazılan sayfa yayınlanmış görünüp 404 dönerdi.
    /// <c>app_data</c> ayrıca <c>NeverTouch</c> listesinde de var — burada tekrar sayılması,
    /// kullanıcının HATA ALMASI (sayfa düşürülür) yerine sessizce yok sayılmaması içindir.
    /// </para>
    /// <para>
    /// <c>con</c>, <c>prn</c>, <c>aux</c>, <c>nul</c>, <c>com1…</c>, <c>lpt1…</c>: Windows'un
    /// AYRILMIŞ AYGIT adları. Bu adlarla klasör açmak işletim sistemi düzeyinde başarısız olur ya
    /// da beklenmedik davranır; panelin başka bir modülünde aynı sınıf zaten bir kez ısırmıştı.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets", "app_data", "app_code", "app_browsers", "app_globalresources", "bin",
        "index", "web", "aspnet_client",
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    /// <summary>Slug geçerli mi (kalıba uyar VE ayrılmış bir ad değil).</summary>
    public static bool IsValid(string? slug)
    {
        var value = (slug ?? string.Empty).Trim();
        if (value.Length == 0 || !SlugPattern().IsMatch(value))
        {
            return false;
        }

        // Ayrılmış adlar aynı zamanda NeverTouch listesiyle de kesişebilir; ikisi de reddeder,
        // ikisi de burada tek çağrıda ölçülür.
        return !Reserved.Contains(value) && !SiteBuilderLayout.IsProtected(value);
    }

    /// <summary>
    /// Sayfanın site köküne göreli dosya yolu. Slug'ın geçerli olduğu ÇAĞIRAN tarafından
    /// doğrulanmış olmalıdır.
    /// </summary>
    public static string ToRelativeFile(string slug) => $"{slug}/{SiteBuilderLayout.IndexFile}";

    /// <summary>
    /// Sayfanın ziyaretçi tarafından görülen adresi: <c>hakkimizda/</c>.
    /// </summary>
    /// <remarks>
    /// Sondaki eğik çizgi ÖNEMLİ: onsuz tarayıcı <c>/hakkimizda</c> için sunucudan bir yönlendirme
    /// bekler ve göreli adresler (CSS, görsel) bir üst klasöre göre çözülür — sayfa stilsiz açılır.
    /// </remarks>
    public static string ToHref(string slug) => $"{slug}/";
}
