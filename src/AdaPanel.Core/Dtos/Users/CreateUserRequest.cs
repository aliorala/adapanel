using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Users;

public sealed class CreateUserRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public string UserName { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;

    [Required]
    [RegularExpression("^(Administrator|Operator|Customer|Reseller)$")]
    public string Role { get; init; } = "Operator";

    /// <summary>Customer rolünde bu müşteriyi yönetecek bayinin (Reseller) kimliği; yoksa null (admin yönetir).</summary>
    public Guid? OwnerId { get; init; }

    [Required]
    [RegularExpression("^(tr|en)(-[A-Z]{2})?$")]
    public string PreferredLanguage { get; init; } = "tr";

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

    /// <summary>true ise kullanıcıya giriş bilgilerini içeren hoş geldin e-postası gönderilir.</summary>
    public bool SendWelcomeEmail { get; init; }
}
