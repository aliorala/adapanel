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

    /// <summary>
    /// ÖKSÜZ dosyalar: kurucunun daha önce yazdığı, hâlâ diskte duran ama bu yayında ARTIK
    /// üretilmeyen dosyalar (kaldırılan/yeniden adlandırılan bir sayfa, tasarımdan çıkarılan bir görsel).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden silinmiyor, gösteriliyor:</b> "artık üretilmeyen dosyayı sil" adımı bu depoda
    /// müşteri sitesini yok edebilecek hata sınıfının ta kendisi — FTP tarafında klasör bağlantısı
    /// üzerinden özyinelemeli silme tam olarak bunu yapabiliyordu. Silme yolunda bir hata GERİ
    /// ALINAMAZ; göstermede en kötü ihtimalle sunucuda birkaç KB fazladan dosya kalır.
    /// </para>
    /// <para>
    /// <b>Ama sessiz de kalınmıyor:</b> kaldırılan bir sayfanın HTML'i adresinde durmaya devam
    /// eder. Kullanıcı sayfayı kurucudan sildiğinde onu sitesinden de sildiğini SANIRSA, bu bir
    /// sürpriz olurdu. Bu liste tam olarak o sürprizi engellemek için var: dosya adıyla ve tam
    /// yoluyla gösterilir, silme kararı sahibinindir.
    /// </para>
    /// </remarks>
    public List<string> OrphanFiles { get; set; } = [];

    /// <summary>
    /// İletişim FORMU istendiği hâlde basılamıyorsa sebebin çeviri anahtarı; boşsa engel yok
    /// (form ya kapalı ya da çalışır durumda).
    /// </summary>
    /// <remarks>
    /// Ayrı bir alan, çünkü bu YAYINI ENGELLEMEZ: sayfa yine yayınlanır, yalnız formsuz. Yayını
    /// engelleyen sebeplerle (<see cref="BlockedReasonKey"/>) aynı alana koymak, düzeltilebilir
    /// bir eksikliği yayını durduran bir arıza gibi gösterirdi.
    /// </remarks>
    public string ContactFormBlockedReasonKey { get; set; } = string.Empty;

    /// <summary>Yayınlanacak sayfa sayısı (ana sayfa dahil).</summary>
    public int PageCount { get; set; }

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
