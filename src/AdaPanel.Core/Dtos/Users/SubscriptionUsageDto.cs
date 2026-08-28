namespace AdaPanel.Core.Dtos.Users;

/// <summary>Bir aboneliğin o anki kullanımı ve durumu — ekranda "ne kadarını harcadım" kartı.</summary>
public sealed class SubscriptionUsageDto
{
    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string PlanId { get; set; } = string.Empty;

    public string PlanName { get; set; } = string.Empty;

    // --- trafik ---

    /// <summary>Bu takvim ayında gönderilen bayt (sitelerin IIS erişim günlüklerinden).</summary>
    public long TrafficBytesThisMonth { get; set; }

    /// <summary>Aylık trafik kotası, GB (0 = sınırsız).</summary>
    public int TrafficLimitGb { get; set; }

    /// <summary>
    /// Trafik ölçülebildi mi. <c>false</c> ise <see cref="TrafficBytesThisMonth"/> GERÇEK
    /// kullanımı göstermez ve kota UYGULANMAZ — ölçemediğimiz için askıya almak, kimsenin
    /// aşmadığı bir kotadan dolayı müşteriyi yayından kaldırmak olurdu.
    /// </summary>
    public bool TrafficMeasured { get; set; }

    // --- süre ---

    public DateTime? ExpiresAtUtc { get; set; }

    public bool IsExpired { get; set; }

    // --- askı ---

    public bool IsSuspended { get; set; }

    public DateTime? SuspendedAtUtc { get; set; }

    /// <summary>Sabit anahtar: "traffic" | "expired" | "manual". Arayüz çevirir.</summary>
    public string SuspendReason { get; set; } = string.Empty;

    // --- diğer kotalar (ekranda tek yerde görünsün diye) ---

    public int SiteCount { get; set; }

    public int MaxSites { get; set; }

    public long DiskUsageMb { get; set; }

    /// <summary>
    /// Disk kullanımı TAM ölçülebildi mi. <c>false</c> ise <see cref="DiskUsageMb"/> gerçek
    /// kullanımı değil bir ALT SINIRI gösterir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Arayüz bunu GÖSTERMEK ZORUNDA:</b> "1,2 GB" yazıp sayının kesildiğini gizlemek, bu
    /// panelde yasaktır — kullanıcı kotasının dolmasına ne kadar kaldığını bu ekrandan okur ve
    /// eksik bir sayı onu yanlış karara götürür (bkz. aynı gerekçe <c>DirectorySizeDto</c>'da).
    /// Kardeşi <see cref="TrafficMeasured"/> ile aynı sözleşme, aynı sebep.
    /// </para>
    /// <para>
    /// <b>Trafikten TEK farkı:</b> ölçülemeyen trafikte kota hiç uygulanmaz (askıya alma
    /// yaptırımdır); disk tarafında ise ölçülen ALT SINIR tavanı aşıyorsa yazma yine reddedilir —
    /// alt sınırın aşması KESİN bilgidir. Gerekçesi <c>FileQuotaGuard.EnsureRoom</c>'da.
    /// </para>
    /// </remarks>
    public bool DiskMeasured { get; set; }

    /// <summary>
    /// Disk ölçümü eksikse sebebinin çeviri anahtarı (<c>files.size_truncated_entries|_time|_denied</c>);
    /// ölçüm tamsa boş.
    /// </summary>
    /// <remarks>
    /// Boş olması "ölçüm tam" demek DEĞİLDİR: sitesi IIS'ten hiç okunamayan aboneliğin eksikliğinin
    /// çevrilmiş bir anahtarı yoktur. Arayüzün bakacağı bayrak <see cref="DiskMeasured"/>'dır; bu
    /// alan yalnız "neden" ayrıntısını verir.
    /// </remarks>
    public string DiskUsageTruncatedReasonKey { get; set; } = string.Empty;

    public int MaxDiskMb { get; set; }
}

/// <summary>Bir denetim turunda durumu değişen abonelik.</summary>
public sealed class SubscriptionEnforcementChangeDto
{
    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>"suspended" | "resumed".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Sabit anahtar: "traffic" | "expired" | "manual".</summary>
    public string Reason { get; set; } = string.Empty;
}
