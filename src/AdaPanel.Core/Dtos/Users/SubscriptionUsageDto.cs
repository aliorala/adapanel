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
