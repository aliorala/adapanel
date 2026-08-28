namespace AdaPanel.Core.Dtos.Users;

public sealed class UserSummaryDto
{
    public Guid Id { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string PreferredLanguage { get; init; } = "tr";

    public bool IsActive { get; init; }

    public bool IsTwoFactorEnabled { get; init; }

    public DateTime? LastLoginAtUtc { get; init; }

    /// <summary>
    /// Hatalı giriş denemeleri yüzünden kilitliyse kilidin bitiş zamanı (UTC); değilse null.
    /// Kullanıcı listesinde görünür ve yönetici tek tıkla kilidi açabilir.
    /// </summary>
    public DateTime? LockedUntilUtc { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Customer rolü için atanmış site adları.</summary>
    public string[] AssignedSites { get; init; } = [];

    public int MaxSites { get; init; }

    public int MaxDiskMb { get; init; }

    /// <summary>Customer için en fazla veritabanı kotası (panel MaxDatabases).</summary>
    public int MaxDatabases { get; init; }

    /// <summary>Customer için en fazla posta kutusu kotası (panel MaxMailboxes).</summary>
    public int MaxMailboxes { get; init; }

    /// <summary>Customer için en fazla planlı görev kotası (panel MaxScheduledTasks).</summary>
    public int MaxScheduledTasks { get; init; }

    /// <summary>Kullanıcı bir alt-kullanıcıysa parent müşterinin kimliği; değilse null.</summary>
    public Guid? ParentUserId { get; init; }

    /// <summary>Alt-kullanıcının parent müşterisinin kullanıcı adı (gösterim için).</summary>
    public string ParentName { get; init; } = string.Empty;

    /// <summary>Customer'ın atanan sitelerinin güncel disk kullanımı (MB).</summary>
    /// <remarks>
    /// <see cref="DiskMeasured"/> <c>false</c> ise bu sayı bir ALT SINIRDIR, gerçek kullanım değil.
    /// </remarks>
    public long DiskUsageMb { get; init; }

    /// <summary>
    /// <see cref="DiskUsageMb"/> TAM ölçülebildi mi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden listede satır satır taşınıyor:</b> kullanım bütçeli bir ağaç yürüyüşüyle ölçülür ve
    /// bütçe abonelik başınadır — yani listedeki bir müşterinin sayısı kesilirken komşusununki tam
    /// olabilir. Tek bir "liste eksik" uyarısı, hangi satıra güvenileceğini söylemezdi.
    /// </para>
    /// <para>
    /// Yönetici bu listeye bakarak kota büyütür, müşteri uyarır, hatta hesap askıya alır. Kesilmiş
    /// bir sayıyı tam gibi göstermek, o kararların hepsini yanlış veriye dayandırırdı — bu depoda
    /// eksik sonucu tam göstermek yasaktır (aynı kural <c>DirectorySizeDto.Truncated</c>'ta).
    /// </para>
    /// </remarks>
    public bool DiskMeasured { get; init; }

    /// <summary>
    /// Ölçüm eksikse sebebinin çeviri anahtarı (<c>files.size_truncated_entries|_time|_denied</c>);
    /// tamsa boş. Boş olması "tam" demek değildir — bakılacak bayrak <see cref="DiskMeasured"/>'dır.
    /// </summary>
    public string DiskUsageTruncatedReasonKey { get; init; } = string.Empty;

    /// <summary>
    /// Customer'a ait güncel veritabanı sayısı (MaxDatabases kotasının kullanılan kısmı). TÜM motorların
    /// (MySQL + MariaDB + PostgreSQL + MSSQL) TOPLAMIDIR — kota motordan bağımsız, toplam DB sayısını sınırlar.
    /// </summary>
    public int DatabaseCount { get; init; }

    /// <summary>Customer'ı yöneten bayinin (Reseller) kimliği; yoksa null (admin yönetir).</summary>
    public Guid? OwnerId { get; init; }

    /// <summary>Sahip bayinin kullanıcı adı (gösterim için); yoksa boş.</summary>
    public string OwnerName { get; init; } = string.Empty;
}
