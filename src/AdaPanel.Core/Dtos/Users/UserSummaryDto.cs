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

    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Customer rolü için atanmış site adları.</summary>
    public string[] AssignedSites { get; init; } = [];

    public int MaxSites { get; init; }

    public int MaxDiskMb { get; init; }

    /// <summary>Customer için en fazla veritabanı kotası (panel MaxDatabases).</summary>
    public int MaxDatabases { get; init; }

    /// <summary>Customer için en fazla posta kutusu kotası (panel MaxMailboxes).</summary>
    public int MaxMailboxes { get; init; }

    /// <summary>Customer'ın atanan sitelerinin güncel disk kullanımı (MB).</summary>
    public long DiskUsageMb { get; init; }

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
