namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// Taramanın KAYNAK SINIRLARI. Tarama, panelin kendi sunucusuna istek atar; sınırsız bir tarayıcı
/// müşterinin sitesini (ve dolayısıyla sunucuyu) kendi paneliyle boğardı.
/// </summary>
/// <remarks>
/// <para>
/// Sınırlar KIRPILIR, reddedilmez: kullanıcı 10.000 sayfa isterse istek hata vermez, üst sınıra
/// iner ve rapor "sınıra dayanıldı" der. Reddetmek kullanıcıyı doğru sayıyı tahmin etmeye zorlar;
/// kırpmak ise hem çalışır hem de ne olduğunu söyler.
/// </para>
/// <para>
/// <b>Neden eşzamanlılık 2 (en fazla 4):</b> tarayıcı ile taranan site AYNI makinededir. Yüksek
/// eşzamanlılık, ölçmeye çalıştığı yanıt süresini kendi yükü ile bozar — "yavaş sayfa" bulgusu
/// tarayıcının kendi ürettiği bir yavaşlığı gösterirdi.
/// </para>
/// </remarks>
public sealed record SeoCrawlLimits(
    int MaxPages,
    int MaxConcurrency,
    TimeSpan RequestTimeout,
    TimeSpan TotalTimeout,
    int MaxResponseBytes)
{
    public const int DefaultMaxPages = 50;

    public const int HardMaxPages = 200;

    public const int DefaultConcurrency = 2;

    public const int HardMaxConcurrency = 4;

    /// <summary>Tek isteğin üst süresi. Yanıt vermeyen bir sayfa taramanın tamamını kilitlememeli.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Taramanın toplam üst süresi. Sayfa sınırı tek başına yetmez: 50 sayfanın her biri 10 saniye
    /// sürerse tur 8 dakikayı aşar ve arka plan işçisi başka taramalara sıra vermez.
    /// </summary>
    public static readonly TimeSpan DefaultTotalTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Sayfa başına okunacak en fazla bayt (2 MB). Sınırsız okuma, tek bir dev dosya ile panelin
    /// belleğini tüketmenin en kolay yoludur.
    /// </summary>
    public const int DefaultMaxResponseBytes = 2 * 1024 * 1024;

    public static SeoCrawlLimits Default { get; } = new(
        DefaultMaxPages,
        DefaultConcurrency,
        DefaultRequestTimeout,
        DefaultTotalTimeout,
        DefaultMaxResponseBytes);

    /// <summary>Kullanıcının istediği sayfa sayısını güvenli aralığa kırpar (0/eksi = varsayılan).</summary>
    public static SeoCrawlLimits FromRequest(int requestedMaxPages)
    {
        var pages = requestedMaxPages <= 0
            ? DefaultMaxPages
            : Math.Min(requestedMaxPages, HardMaxPages);

        return Default with { MaxPages = pages };
    }
}
