namespace AdaPanel.Core.Dtos.Users;

/// <summary>
/// Bir bayinin (Reseller) KENDİ tavanı: dağıtabileceği toplam kaynak.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir kayıt:</b> panelde bayi yalnızca <i>müşterilerine</i> kota verebiliyordu,
/// kendisine bir tavan atanamıyordu. Yani "bu bayiye 50 site / 100 GB sattım" diye bir kavram
/// yoktu; bayi istediği kadar müşteri açıp her birine sınırsız plan bağlayabiliyordu. Bayi
/// kanalının iş modeli tam olarak bu tavana dayanır.
/// </para>
/// <para>
/// <b>Neden <c>customer-scopes.json</c>'a yazılmadı:</b> ölçüldü —
/// <c>UserManagementService.ApplyScopeAsync</c> müşteri OLMAYAN her kullanıcı için kapsam kaydını
/// <c>RemoveUserAsync</c> ile SİLİYOR. Bayi tavanı oraya yazılsaydı, bayi kullanıcısı her
/// düzenlendiğinde tavan sessizce yok olurdu — yani kota var görünüp fiilen hiç uygulanmazdı.
/// Ayrı ve sahibi belli bir kayıt bu sınıf hatayı kökünden kaldırır.
/// </para>
/// <para>0 = sınırsız (panelin diğer bütün limitleriyle aynı anlam).</para>
/// </remarks>
public sealed class ResellerQuotaDto
{
    public Guid ResellerId { get; set; }

    /// <summary>Bayinin dağıtabileceği toplam site sayısı (0 = sınırsız).</summary>
    public int MaxSites { get; set; }

    /// <summary>Bayinin dağıtabileceği toplam disk alanı, MB (0 = sınırsız).</summary>
    public int MaxDiskMb { get; set; }
}

/// <summary>Bayi tavanı atama isteği (yalnız yönetici).</summary>
public sealed class ResellerQuotaRequest
{
    public int MaxSites { get; set; }

    public int MaxDiskMb { get; set; }
}

/// <summary>
/// Bayi tavanının ne kadarının DAĞITILDIĞI ve ne kadarının GERÇEKTEN kullanıldığı.
/// </summary>
/// <remarks>
/// Dağıtılan ile kullanılan bilerek AYRI: bayi 50 sitelik tavanının 50'sini müşterilerine
/// dağıtmış ama yalnız 12'si açılmış olabilir. Tavan denetimi DAĞITILANA bakar (aşırı satışı
/// engeller), kullanıcıya ise ikisi de gösterilir.
/// </remarks>
public sealed class ResellerQuotaUsageDto
{
    public Guid ResellerId { get; set; }

    public int MaxSites { get; set; }

    public int MaxDiskMb { get; set; }

    /// <summary>Müşterilere dağıtılan site hakkı + bayinin kendi doğrudan sitelerinin sayısı.</summary>
    public int AllocatedSites { get; set; }

    /// <summary>Müşterilere dağıtılan disk hakkı, MB.</summary>
    public int AllocatedDiskMb { get; set; }

    /// <summary>Bayinin etkin kapsamında GERÇEKTEN var olan site sayısı.</summary>
    public int UsedSites { get; set; }

    /// <summary>Bayinin müşteri sayısı.</summary>
    public int CustomerCount { get; set; }

    /// <summary>
    /// Müşterilerden en az birinin limiti SINIRSIZ (0) mı. Tavanı olan bir bayide bu, tavanın
    /// fiilen delindiği anlamına gelir; bu yüzden ayrıca raporlanır.
    /// </summary>
    public bool HasUnlimitedCustomer { get; set; }
}
