namespace AdaPanel.Core.Dtos.Users;

/// <summary>
/// Müşterilere atanabilen yeniden kullanılabilir limit şablonu (servis planı).
/// </summary>
/// <remarks>
/// <para>
/// <b>Plan bir ŞABLON değil, BAĞDIR.</b> Önceden plan yalnızca müşteri oluşturulurken forma
/// kopyalanıyordu; kalıcı bir bağ yoktu. Sonucu: planı değiştirmek mevcut müşterilerin hiçbirini
/// etkilemiyordu — 200 müşterili bir sunucuda "Başlangıç planının disk alanını 5 GB'a çıkar"
/// demek, 200 müşteriyi tek tek elle düzenlemek anlamına geliyordu.
/// </para>
/// <para>
/// Artık abonelikte plan kimliği saklanıyor ve limitler <b>okuma anında plandan çözülüyor</b>.
/// Plan değişince bağlı bütün abonelikler aynı anda değişir.
/// </para>
/// </remarks>
public sealed class ServicePlanDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Planı TANIMLAYAN bayinin kimliği ("N" biçimi); boş ise plan SİSTEM planıdır (yöneticinin).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bayi kanalı bu alan olmadan işlemiyordu: <c>ServicePlansController</c> tümüyle
    /// <c>Administrator</c>'a kilitliydi, yani bayi kendi paket/fiyat listesini tanımlayamıyor,
    /// her yeni paket için yöneticiye başvurmak zorunda kalıyordu. Plesk'te bayinin ilk yaptığı iş
    /// kendi planlarını kurmaktır.
    /// </para>
    /// <para>
    /// Alan aynı zamanda İZOLASYONUN dayanağı: bayi yalnız kendi sahipli planlarını
    /// düzenleyip silebilir, sistem planlarını ve başka bayinin planlarını göremez/değiştiremez.
    /// </para>
    /// <para>
    /// <c>ServicePlanRequest</c>'e BİLEREK eklenmedi: sahiplik istekten okunsaydı bayi, gövdeye
    /// başka bir kimlik yazarak planı başkasının üstüne geçirebilirdi. Sahibi sunucu, oturumdaki
    /// kimlikten belirler.
    /// </para>
    /// </remarks>
    public string OwnerId { get; set; } = string.Empty;

    public int MaxSites { get; set; }

    public int MaxDiskMb { get; set; }

    /// <summary>En fazla veritabanı (0 = sınırsız).</summary>
    public int MaxDatabases { get; set; }

    /// <summary>En fazla posta kutusu (0 = sınırsız).</summary>
    public int MaxMailboxes { get; set; }

    /// <summary>En fazla planlı görev (0 = sınırsız).</summary>
    public int MaxScheduledTasks { get; set; }

    /// <summary>En fazla FTP hesabı (0 = sınırsız).</summary>
    public int MaxFtpUsers { get; set; }

    /// <summary>
    /// Aylık trafik kotası, GB (0 = sınırsız). Aşıldığında abonelik otomatik askıya alınır.
    /// <para>
    /// Ölçüm, sitenin IIS erişim günlüklerinden gelir (gönderilen bayt) ve <b>takvim ayı</b>
    /// başında sıfırlanır — barındırma sektöründe yerleşik olan sayım budur.
    /// </para>
    /// </summary>
    public int MaxTrafficGbPerMonth { get; set; }

    /// <summary>
    /// Aboneliğin geçerlilik süresi, gün (0 = süresiz). Müşteriye atandığında bitiş tarihi
    /// buradan hesaplanır; süresi dolan abonelik otomatik askıya alınır.
    /// </summary>
    public int ValidityDays { get; set; }

    /// <summary>
    /// Aboneliğin GÜNDE en fazla kaç ZAMANLANMIŞ yedek alabileceği (0 = sınırsız).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu, satılabilir bir hizmet farkıdır: "günde 2 kez yedek" üst paketin özelliğidir, çünkü her
    /// yedek turu disk, CPU ve (off-site hedefte) bant genişliği harcar. Sınır olmadan tek bir
    /// müşteri saatlik program kurup sunucunun bütün disk alanını tüketebilir.
    /// </para>
    /// <para>
    /// Sınır PROGRAM KURULURKEN uygulanır (aralık, günde bu sayıdan fazla tura yol açamaz) ve
    /// ayrıca her turda TEKRAR ölçülür: plan sonradan daraltılırsa mevcut program kendiliğinden
    /// kotaya çekilir — kaydedilmiş eski bir aralığın kotayı sonsuza kadar delmesi kabul edilemez.
    /// </para>
    /// <para>
    /// ELLE alınan yedekler bu sayıya girmez: müşteri riskli bir işlemden önce yedek almak
    /// istediğinde onu engellemek, korumanın kendisini engellemek olurdu.
    /// </para>
    /// </remarks>
    public int MaxBackupsPerDay { get; set; }
}

public sealed class ServicePlanRequest
{
    public string Name { get; set; } = string.Empty;

    public int MaxSites { get; set; }

    public int MaxDiskMb { get; set; }

    public int MaxDatabases { get; set; }

    public int MaxMailboxes { get; set; }

    public int MaxScheduledTasks { get; set; }

    public int MaxFtpUsers { get; set; }

    public int MaxTrafficGbPerMonth { get; set; }

    public int ValidityDays { get; set; }

    /// <summary>Günde en fazla kaç zamanlanmış yedek (0 = sınırsız).</summary>
    public int MaxBackupsPerDay { get; set; }
}
