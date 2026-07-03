using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Users;

public sealed class UpdateUserRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [RegularExpression("^(Administrator|Operator|Customer|Reseller)$")]
    public string Role { get; init; } = "Operator";

    /// <summary>Customer rolünde bu müşteriyi yönetecek bayinin (Reseller) kimliği; yoksa null.</summary>
    public Guid? OwnerId { get; init; }

    [Required]
    [RegularExpression("^(tr|en)(-[A-Z]{2})?$")]
    public string PreferredLanguage { get; init; } = "tr";

    public bool IsActive { get; init; } = true;

    /// <summary>Customer rolü için erişebileceği site adları.</summary>
    public string[] AssignedSites { get; init; } = [];

    /// <summary>Customer için en fazla atanabilecek site sayısı (0 = sınırsız).</summary>
    public int MaxSites { get; init; }

    /// <summary>Customer için toplam disk kotası MB (0 = sınırsız).</summary>
    public int MaxDiskMb { get; init; }

    /// <summary>Customer için en fazla veritabanı kotası (panel MaxDatabases; 0 = sınırsız).</summary>
    public int MaxDatabases { get; init; }

    /// <summary>Customer için en fazla posta kutusu kotası (panel MaxMailboxes; 0 = sınırsız).</summary>
    public int MaxMailboxes { get; init; }

    /// <summary>Customer için en fazla planlı görev kotası (panel MaxScheduledTasks; 0 = sınırsız).</summary>
    public int MaxScheduledTasks { get; init; }
}
