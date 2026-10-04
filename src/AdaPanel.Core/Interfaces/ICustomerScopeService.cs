namespace AdaPanel.Core.Interfaces;

/// <summary>Bir müşteri hesabının kaynak kapsamı: atanmış siteler + limitler.</summary>
/// <summary>
/// Bir aboneliğin etkin kaynak kapsamı ve durumu.
/// </summary>
/// <remarks>
/// Yeni alanlar SONA ve varsayılanlı eklendi: mevcut çağrı yerleri (testler dahil) olduğu gibi
/// derlenmeye devam etsin diye. Limitler bir plana bağlıysa <see cref="PlanId"/> doludur ve
/// değerler okuma anında PLANDAN çözülür — plan değişince bağlı bütün abonelikler değişir.
/// </remarks>
public sealed record CustomerScope(
    IReadOnlyList<string> Sites,
    int MaxSites,
    int MaxDiskMb,
    int MaxDatabases,
    int MaxMailboxes,
    int MaxScheduledTasks,
    int MaxFtpUsers = 0,
    int MaxTrafficGbPerMonth = 0,
    string PlanId = "",
    string PlanName = "",
    DateTime? ExpiresAtUtc = null,
    DateTime? SuspendedAtUtc = null,
    string SuspendReason = "")
{
    /// <summary>Abonelik şu an askıda mı (askı, giriş engelinden AYRI bir durumdur).</summary>
    public bool IsSuspended => SuspendedAtUtc is not null;

    /// <summary>Süresi dolmuş mu (süresiz aboneliklerde daima false).</summary>
    public bool IsExpired => ExpiresAtUtc is { } bitis && bitis <= DateTime.UtcNow;
}

/// <summary>
/// Bir aboneliğin disk kullanımı — VE o sayının TAM ölçüm mü yoksa ALT SINIR mı olduğu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden düz bir sayı DEĞİL (kapatılan açık):</b> kullanım bütçeli bir ağaç yürüyüşüyle ölçülür
/// (<c>SafeDirectoryWalk</c>). Yürüyüş öge/süre bütçesine takılırsa, bir klasör okunamazsa ya da bir
/// site IIS'ten hiç okunamazsa dönen sayı gerçek kullanımın ALT SINIRIDIR. Bu bilgi KAYNAKTA VARDI
/// (<c>SafeDirectoryWalkResult.Truncated</c>) ama dönüş tipi taşımadığı için YOLDA KAYBOLUYORDU:
/// zorlama yolu alt sınırı kesin bir ölçüymüş gibi karşılaştırıyor, kullanıcı listesi ve fatura
/// ekranı da eksik sayıyı TAM değermiş gibi yazıyordu — yani panel, bilmediği bir şeyi biliyormuş
/// gibi gösteriyordu.
/// </para>
/// <para>
/// <b>Kalıp uydurulmadı, panelin İKİNCİ kotasından kopyalandı:</b> trafik kotası aynı soruyu
/// <c>SubscriptionEnforcementService.TrafikOlcAsync</c>'te <c>(Bytes, Measured)</c> ikilisiyle
/// cevaplar ve ölçülemeyen abonelikte kotayı UYGULAMAZ (<c>SubscriptionUsageDto.TrafficMeasured</c>).
/// İki kotanın iki ayrı felsefeyle çalışması, birini sıkılaştırıp diğerini unutmanın yoludur.
/// </para>
/// </remarks>
/// <param name="UsageMb">Ölçülen kullanım (MB). <paramref name="Measured"/> <c>false</c> ise bu bir
/// ALT SINIRDIR: gerçek kullanım EN AZ bu kadardır.</param>
/// <param name="Measured">Ölçüm tam mı? <c>false</c> ise ağacın bir kısmına HİÇ bakılamadı.</param>
/// <param name="TruncatedReasonKey">
/// Eksikliğin sebebinin çeviri anahtarı (<c>files.size_truncated_entries|_time|_denied</c>); ölçüm
/// tamsa boş. <b>Boş olması "ölçüm tam" demek DEĞİLDİR</b> — tek doğru soru
/// <paramref name="Measured"/>'dır; sebebi olmayan tek eksiklik hâli, sitenin IIS'ten hiç
/// okunamamasıdır (o durumun çevrilmiş bir anahtarı yok ve olmayan bir anahtar uydurmak,
/// kullanıcının ekranda ham anahtar görmesi demekti).
/// </param>
public readonly record struct DiskUsage(long UsageMb, bool Measured, string TruncatedReasonKey)
{
    /// <summary>
    /// Observed bytes before conversion to MiB. This is still a lower bound when
    /// Measured is false; null preserves compatibility with older measurement providers.
    /// </summary>
    public long? UsageBytes { get; init; }
}

/// <summary>
/// Müşteri panelinin bölüm anahtarları (alt-kullanıcı izin matrisinin evreni).
/// Frontend menü kimlikleriyle birebir aynıdır; home/settings/exit her zaman açıktır, listelenmez.
/// </summary>
public static class CustomerPanelSections
{
    public static readonly IReadOnlyList<string> All = ["sites", "mail", "database", "files", "backup", "tasks"];

    public static bool IsKnown(string section) =>
        All.Contains(section, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Customer rolündeki kullanıcıların erişebileceği siteler ve kaynak limitlerinin
/// kalıcı kaydı (config/customer-scopes.json). Yetki ve kota denetimi bunun üzerinden yapılır.
/// </summary>
public interface ICustomerScopeService
{
    Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default);

    Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default);

    /// <summary>Müşterinin ek kaynak kotaları (panel MaxDatabases / MaxMailboxes / MaxScheduledTasks). 0 = limitsiz.</summary>
    Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default);

    Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Bir site silindiğinde tüm müşteri atamalarından o siteyi kaldırır (yetim atama kalmaz).</summary>
    Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Atanan sitelerin adları + bağlama (binding) host'larından türetilen mail domain kümesi.</summary>
    Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Müşterinin atanan sitelerinin toplam disk kullanımı (MB) — ve o sayının ÖLÇÜLDÜĞÜ mü yoksa
    /// bir ALT SINIR mı olduğu (<see cref="DiskUsage"/>).
    /// </summary>
    Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default);

    // --- Bayi (Reseller) sahipliği ---

    /// <summary>Bir müşteriyi bir bayiye (sahip) bağlar. ownerId null ise sahiplik kaldırılır.</summary>
    Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default);

    /// <summary>Bir müşterinin sahibi (bayi) kim; yoksa null.</summary>
    Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Verilen bayinin sahip olduğu müşteri kullanıcı kimlikleri.</summary>
    Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>Bayinin etkin site kümesi: sahip olduğu tüm müşterilerin atanan sitelerinin birleşimi.</summary>
    Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default);

    /// <summary>Bayinin etkin mail domain kümesi: müşterilerinin izinli mail domainlerinin birleşimi.</summary>
    Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default);

    /// <summary>Verilen siteyi atanmış olarak içeren ilk müşteri (kota sahibi) kimliği; yoksa null.</summary>
    Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default);

    // --- Abonelik altı ek kullanıcı (Plesk "additional user") ---

    /// <summary>
    /// Bir kullanıcıyı başka bir müşterinin (parent) aboneliğine "ek kullanıcı" olarak bağlar; o andan itibaren
    /// parent'ın sitelerini/kotalarını MIRAS alır (kendi kaynağı olmaz). parentUserId null ise bağ kaldırılır.
    /// </summary>
    Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default);

    /// <summary>Kullanıcı bir alt-kullanıcıysa bağlı olduğu parent müşteri kimliği; değilse null.</summary>
    Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Verilen parent müşterinin altındaki tüm alt-kullanıcı kimlikleri.</summary>
    Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Alt-kullanıcının panel bölümü izinleri (Plesk "additional user" izin matrisi karşılığı).
    /// null = kısıtsız (tüm müşteri bölümleri). Bilinmeyen bölüm anahtarları yok sayılır.
    /// </summary>
    Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default);

    /// <summary>Alt-kullanıcının izinli bölümleri; null = kısıtsız veya alt-kullanıcı değil.</summary>
    Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default);

    // --- Abonelik iş modeli: plan bağı, süre, askıya alma ---

    /// <summary>
    /// Aboneliği bir servis planına BAĞLAR (planId boş ise bağı kaldırıp özel limitlere döner).
    /// </summary>
    /// <remarks>
    /// Plan bağlıyken limitler okuma anında plandan çözülür; bu yüzden planı düzenlemek bağlı
    /// bütün müşterileri anında etkiler. Planın <c>ValidityDays</c> değeri varsa bitiş tarihi de
    /// buradan hesaplanır.
    /// </remarks>
    Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default);

    /// <summary>Aboneliğin bitiş tarihini elle ayarlar (null = süresiz).</summary>
    Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aboneliği askıya alır. <paramref name="reason"/> sabit anahtardır: "traffic" | "expired" | "manual".
    /// </summary>
    /// <remarks>
    /// Askı, giriş engelinden (IsActive) AYRI bir durumdur: müşteri panele girip faturasını görebilmeli
    /// ama siteleri yayında olmamalıdır. Siteleri durdurmak çağıranın işidir (SuspensionEnforcer).
    /// </remarks>
    Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Askıyı kaldırır.</summary>
    Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Askıda olan bütün aboneliklerin kimlikleri.</summary>
    Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Plana bağlı olsun olmasın, TOP-LEVEL bütün abonelik kimlikleri (alt-kullanıcılar hariç).</summary>
    Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default);
}
