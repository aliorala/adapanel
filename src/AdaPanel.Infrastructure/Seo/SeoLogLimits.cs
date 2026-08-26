namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// Günlük analizinin KAYNAK SINIRLARI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekli — ölçülmüş büyüklük:</b> log döndürme özelliğinin ölçümünde tek bir sitenin
/// TEK GÜNLÜK IIS erişim günlüğü <b>38,3 MB</b>'tı (bkz. <c>LogRotationService</c> açıklaması).
/// Yoğun bir sitede 30 günlük pencere gigabaytlarca metin eder. Dosyayı belleğe almak
/// (<c>ReadAllLines</c>) paneli düşürürdü; sınırsız akıtmak ise bir HTTP isteğini dakikalarca
/// açık tutardı. Bu yüzden sınır ÜÇ eksende birden konur: dosya, satır ve süre.
/// </para>
/// <para>
/// <b>Sınırlar KIRPILIR, reddedilmez</b> (kalıp: <see cref="SeoCrawlLimits"/>): kullanıcı 365 gün
/// isterse istek hata vermez, üst sınıra iner. Sınıra dayanıldığında rapor bunu
/// <c>LimitReached</c> ile SÖYLER — kırpılmış bir okumayı tam sanmak, "Googlebot hiç gelmemiş"
/// gibi yanlış bir SEO kararına yol açardı.
/// </para>
/// </remarks>
public static class SeoLogLimits
{
    /// <summary>Varsayılan pencere. Bir haftalık tarama davranışı, günlük dalgalanmayı yumuşatır.</summary>
    public const int DefaultDays = 7;

    /// <summary>
    /// En uzun pencere. 30 gün seçildi çünkü log döndürmenin site günlükleri için varsayılan yaş
    /// eşiği de 30 gündür — daha uzun bir pencere istemek, diskte artık BULUNMAYAN günleri
    /// istemek olurdu.
    /// </summary>
    public const int MaxDays = 30;

    /// <summary>
    /// Açılacak en fazla dosya. 30 günlük pencerede günde bir ham + bir arşiv dosyası olsa bile
    /// 60 yeter; daha fazlası klasöre elle atılmış eski günlükler demektir ve pencerenin dışıdır.
    /// </summary>
    public const int MaxFiles = 60;

    /// <summary>
    /// Okunacak en fazla satır. <c>WebStatisticsService</c> ile AYNI değer (2 milyon) bilinçli
    /// seçildi: iki modül aynı dosyaları okur, farklı bütçe koymak aynı sitede iki farklı
    /// "tamlık" anlamına gelirdi.
    /// </summary>
    public const int MaxLines = 2_000_000;

    /// <summary>
    /// Analizin toplam üst süresi. Satır sınırı tek başına yetmez: arşivlerin açılması ve disk
    /// hızı da süreye girer, ve bu iş bir HTTP isteğinin içinde koşar.
    /// </summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Sözlüklerin (yol, sorgu) üst sınırı. Anahtarlar İSTEMCİNİN yazdığı değerlerdir ve IIS
    /// 404'leri de günlüğe yazar; bir güvenlik tarayıcısı sınırsız bir sözlüğü tek turda yüzlerce
    /// MB'a çıkarır. Kalıp <c>WebStatisticsService</c>'ten alındı.
    /// </summary>
    public const int MaxDistinctPaths = 50_000;

    /// <summary>Listelerde gösterilen üst sayı (en çok gezilen sayfa, israf kalemi).</summary>
    public const int TopCount = 25;

    /// <summary>
    /// Ters DNS sorgusu yapılacak en fazla IP. Doğrulama AĞ işidir; sınırsız bırakmak, analizi
    /// DNS sunucusunun hızına teslim etmek olurdu. En çok istek atan IP'ler önce sorulur.
    /// </summary>
    public const int MaxVerifiedIps = 25;

    /// <summary>Tek ters DNS sorgusunun üst süresi.</summary>
    public static readonly TimeSpan ReverseDnsTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Doğrulamanın TOPLAM üst süresi — cevapsız bir DNS sunucusu analizi kilitlememeli.</summary>
    public static readonly TimeSpan VerificationBudget = TimeSpan.FromSeconds(20);

    /// <summary>Kullanıcının istediği pencereyi güvenli aralığa kırpar (0/eksi = varsayılan).</summary>
    public static int NormalizeDays(int requestedDays) =>
        requestedDays <= 0 ? DefaultDays : Math.Min(requestedDays, MaxDays);
}
