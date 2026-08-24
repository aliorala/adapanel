namespace AdaPanel.Core.Dtos.RunningJobs;

/// <summary>
/// İş TÜRLERİ — sunucu, denetleyiciler ve testler için TEK KAYNAK.
/// <para>
/// Değerler arayüzün i18n anahtarlarına da giriyor (<c>panel.runningJob.backup</c> gibi); serbest
/// dizge olarak dört ayrı yerde yazılsalardı bir harf farkı, iş kaydını sessizce görünmez kılardı.
/// </para>
/// </summary>
public static class RunningJobKinds
{
    /// <summary>Yedek alma (site/db/mail/panel/abonelik).</summary>
    public const string Backup = "backup";

    /// <summary>Geri yükleme (tam ve granüler).</summary>
    public const string Restore = "restore";

    /// <summary>Uzak sunucudan site göçü.</summary>
    public const string Migration = "migration";

    /// <summary>Bileşen kurulumu (PHP, MariaDB gibi büyük indirmeler).</summary>
    public const string Software = "software";
}

/// <summary>
/// ŞU ANDA sunucuda süren uzun iş; sayfa yenilendikten sonra arayüzün "sürüyor" bilgisini geri
/// okuduğu tek kaynak.
/// <para>
/// <b>YÜZDE ALANI YOK — ve bilerek yok.</b> Bu sözleşmenin kapsadığı işlerin hiçbiri ölçülebilir
/// bir ilerleme yayımlamıyor: yedekleme sıkıştırırken toplam dosya sayısını önceden bilmiyor,
/// robocopy/FTP göçünde uzak taraftaki dosya sayısı çekilmeden bilinmiyor, bileşen kurulumunda
/// indirilen paketin boyutu sunucudan gelmeyebiliyor. Yüzde göstermek için tek yol onu UYDURMAK
/// olurdu (geçen süreyi varsayılan bir süreye bölmek gibi) ve o sayı hem 2 dakikalık hem 2 saatlik
/// işte yanlış olurdu. Bunun yerine ÖLÇÜLEBİLEN iki şey verilir: işin ne olduğu ve ne zaman
/// başladığı. Aynı kural antivirüs taramasında ve panel güncellemesinde de uygulanıyor
/// (bkz. <c>AntivirusRunningScanDto</c>, <c>panel.update.phaseHintReal</c>).
/// </para>
/// </summary>
public sealed class RunningJobDto
{
    /// <summary>İş türü: <c>backup</c> | <c>restore</c> | <c>migration</c> | <c>software</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>İşin konusu (site adı, yedek adı, bileşen kimliği). Arayüz bunu etikete yazar.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>İşin SUNUCUDA başladığı an; geçen süre buradan hesaplanır.</summary>
    public DateTime StartedAtUtc { get; init; }
}

/// <summary>
/// YARIDA KALMIŞ iş: panel süreci iş sürerken yeniden başladı, iş o süreçle birlikte gitti ve
/// sonucu HİÇ YAZILAMAYACAK.
/// <para>
/// <b>NEDEN AYRI BİR KAYIT:</b> diskteki "sürüyor" işaretini sessizce silmek, kullanıcıya iki saat
/// beklediği yedeğin neden ortada olmadığını hiç söylememek olurdu. Ekran boş kalmamalı: "kesildi"
/// durumu yüzeye çıkar, kullanıcı işi yeniden başlatması gerektiğini öğrenir.
/// </para>
/// </summary>
public sealed class InterruptedJobDto
{
    public string Kind { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Kesilen işin başlangıç anı (kayıttan okunur).</summary>
    public DateTime StartedAtUtc { get; init; }

    /// <summary>Kesilmenin FARK EDİLDİĞİ an — panel yeniden başladıktan sonraki ilk okuma.</summary>
    public DateTime DetectedAtUtc { get; init; }
}

/// <summary>
/// Bir modülün çalışan-iş durumu (<c>GET .../running</c>).
/// </summary>
public sealed class RunningJobsStatusDto
{
    public IReadOnlyCollection<RunningJobDto> Running { get; init; } = [];

    public IReadOnlyCollection<InterruptedJobDto> Interrupted { get; init; } = [];

    /// <summary>
    /// Sunucunun şu anki saati (UTC).
    /// <para>
    /// <b>NEDEN GÖNDERİLİYOR:</b> geçen süre <c>StartedAtUtc</c> ile ŞİMDİ arasındaki farktır ve
    /// tarayıcının saati sunucununkinden dakikalarca sapabilir (saati elle kurulmuş makineler,
    /// NTP'siz sunucular). Fark tarayıcının saatiyle hesaplansaydı panel "3 saattir sürüyor" ya da
    /// negatif süre yazabilirdi. Sunucu kendi "şimdi"sini de verince arayüz farkı SUNUCU
    /// ZAMANINDA hesaplar ve yalnızca yerel geçen süreyi üstüne ekler — saat sapması tamamen düşer.
    /// </para>
    /// </summary>
    public DateTime ServerTimeUtc { get; init; }
}
