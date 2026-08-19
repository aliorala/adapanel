namespace AdaPanel.Core.Dtos.Logs;

/// <summary>
/// Log döndürme (rotation) politikası.
/// </summary>
/// <remarks>
/// <para>
/// <b>Varsayılanların gerekçesi ölçümdür, tercih değil:</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="SiteLogMaxAgeDays"/> = 30, çünkü panelin istatistik ekranı en fazla <b>30 günlük</b>
/// pencere sorabiliyor (SiteStatisticsView: 1 / 7 / 30) ve <c>WebStatisticsService</c> bu pencereyi
/// site klasöründeki HAM <c>*.log</c> dosyalarından okur. Daha kısa bir varsayılan, sıkıştırılan
/// günlerin istatistikten SESSİZCE düşmesi demek olurdu — panel "trafik yok" der, oysa veri
/// arşivdedir.
/// </item>
/// <item>
/// <see cref="PanelLogMaxAgeDays"/> = 2, çünkü panel günlüğü ekranda yalnızca EN YENİ dosyadan
/// okunur (<c>LogViewerService.TailPanelLogAsync</c>); eski günleri sıkıştırmak hiçbir ekranı
/// etkilemez.
/// </item>
/// <item>
/// <see cref="SiteKeepCount"/> ve <see cref="MaxTotalSizeMB"/> varsayılan olarak KAPALIDIR (0).
/// Açık varsayılanla gelseydi panel, yöneticinin hiç istemediği bir anda erişim günlüğünü SİLMİŞ
/// olurdu. Site tarafında varsayılan davranış yalnızca sıkıştırmadır: yer kazandırır, hiçbir
/// kaydı yok etmez.
/// </item>
/// <item>
/// <see cref="PanelKeepCount"/> = 14 ise BİLİNÇLİ bir istisnadır ve gerekçesi
/// <see cref="PanelKeepCount"/> üzerinde yazılıdır: panel günlüğünün saklama süresi ZATEN 14 gündü,
/// bu ayar o sözü aynen sürdürür.
/// </item>
/// </list>
/// </remarks>
public sealed class LogRotationSettingsDto
{
    /// <summary>Döndürme açık mı. Kapalıyken arka plan işçisi hiçbir dosyaya dokunmaz.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Döndürülen dosya ZSTD ile sıkıştırılsın mı. Kapalıysa hiçbir dosya sıkıştırılmaz ve
    /// yalnızca saklama (adet/toplam boyut) kuralları çalışır.
    /// </summary>
    public bool Compress { get; set; } = true;

    /// <summary>Site (IIS) günlükleri için yaş eşiği; 0 = yaşa göre döndürme kapalı.</summary>
    public int SiteLogMaxAgeDays { get; set; } = 30;

    /// <summary>Panelin kendi günlükleri için yaş eşiği; 0 = yaşa göre döndürme kapalı.</summary>
    public int PanelLogMaxAgeDays { get; set; } = 2;

    /// <summary>
    /// Boyut eşiği (MB); bu boyutu aşan KAPALI dosya, yaşı ne olursa olsun döndürülür.
    /// 0 = boyuta göre döndürme kapalı.
    /// </summary>
    /// <remarks>
    /// Varsayılan 0: açık olsaydı yoğun bir sitenin dünkü günlüğü hemen sıkıştırılır ve
    /// istatistik ekranından düşerdi — hem de tam olarak istatistiği en çok merak edilen sitede.
    /// </remarks>
    public int MaxFileSizeMB { get; set; }

    /// <summary>Site klasörü başına saklanacak arşiv sayısı; 0 = sınırsız (hiçbir arşiv silinmez).</summary>
    public int SiteKeepCount { get; set; }

    /// <summary>
    /// Panel günlük klasöründe saklanacak arşiv sayısı; 0 = sınırsız.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden 14 ve neden site tarafından AYRI:</b> panel günlüğünü Serilog yazıyor ve
    /// <c>retainedFileCountLimit: 14</c> ile <b>zaten 14 günlük bir saklama sözü veriyor</b>
    /// (Program.cs). Serilog bu temizliği <c>adapanel-*.log</c> desenine göre yapar; biz bir günü
    /// <c>.log.zst</c> hâline getirdiğimizde dosya o desene UYMAZ ve o günün kaydı Serilog'un
    /// politikasından SESSİZCE kaçardı — yani var olan bir saklama sözü, kimse istemeden
    /// "sonsuza kadar sakla"ya dönerdi. Panel günlüğü IP ve kullanıcı adı taşıdığı için bu, teknik
    /// bir ayrıntı değil saklama politikası değişikliğidir.
    /// </para>
    /// <para>
    /// Bu yüzden arşiv tarafı aynı sayıyı devralır: davranış korunur, yalnızca aynı 14 gün
    /// yaklaşık onda bir yer kaplar. Site günlüklerinde aynı varsayılan verilemezdi — orada
    /// panelin verilmiş bir sözü yok ve 14 arşivle sınırlamak erişim kaydını silmek olurdu.
    /// </para>
    /// </remarks>
    public int PanelKeepCount { get; set; } = 14;

    /// <summary>Klasör başına toplam üst sınır (MB); aşılırsa EN ESKİ arşivler silinir. 0 = kapalı.</summary>
    public int MaxTotalSizeMB { get; set; }

    /// <summary>İki tur arasındaki en az süre (saat).</summary>
    public int IntervalHours { get; set; } = 24;
}
