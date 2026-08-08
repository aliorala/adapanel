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
}
