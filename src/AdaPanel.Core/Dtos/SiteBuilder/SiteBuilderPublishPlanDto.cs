namespace AdaPanel.Core.Dtos.SiteBuilder;

/// <summary>
/// "Yayınla" düğmesine basılmadan ÖNCE gösterilen plan: nereye yazılacak, ne ezilecek.
/// </summary>
/// <remarks>
/// <para>
/// Bu ayrı bir uç olarak var, çünkü yayın MÜŞTERİNİN DOSYALARININ ÜZERİNE YAZAR. Kullanıcıya
/// "emin misiniz?" diye sormak tek başına bir koruma değildir — neyin ezileceğini ADIYLA görmeden
/// verilen onay, bilgisiz bir onaydır. Plan uçu, onay kutusunun içini doldurur.
/// </para>
/// <para>
/// Plan ayrıca YAZMAZ: yalnız okur. Kullanıcı planı görüp vazgeçtiğinde diskte hiçbir şey
/// değişmemiş olur.
/// </para>
/// </remarks>
public sealed class SiteBuilderPublishPlanDto
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Yayının yazılacağı klasör (sitenin uygulama kökü).</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>Yayında oluşturulacak/güncellenecek dosyaların göreli yolları.</summary>
    public List<string> FilesToWrite { get; set; } = [];

    /// <summary>
    /// Kurucunun ÜRETMEDİĞİ, hâlihazırda orada duran ve üzerine yazılacak dosyalar.
    /// Boş değilse yayın açık onay ister.
    /// </summary>
    public List<string> Conflicts { get; set; } = [];

    /// <summary>Hedef klasördeki toplam dosya sayısı (kullanıcı "burada bir site var mı" sorusunu görsün).</summary>
    public int ExistingFileCount { get; set; }

    /// <summary>Onay olmadan yayın reddedilecek mi.</summary>
    public bool RequiresConfirmation { get; set; }

    /// <summary>
    /// Yayın hiç yapılamıyorsa sebebin çeviri anahtarı (ör. hedef klasör bir bağlantı noktası).
    /// Boşsa engel yok.
    /// </summary>
    public string BlockedReasonKey { get; set; } = string.Empty;

    public bool CanPublish => string.IsNullOrEmpty(BlockedReasonKey);
}
