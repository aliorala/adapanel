using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// ABONELİK bazında yedek isteği: bir müşterinin bütün siteleri + veritabanları + posta alan
/// adları + FTP hesapları TEK arşivde.
/// </summary>
/// <remarks>
/// Site yedeği (<see cref="BackupSiteRequest"/>) KALDIRILMADI: tek bir sitenin dünkü hâlini geri
/// almak, bütün aboneliği geri yüklemekten çok daha sık ihtiyaçtır. İkisi de anlamlıdır ve ikisi
/// de aynı arşiv biçimini, aynı şifrelemeyi, aynı zincir kurallarını kullanır.
/// </remarks>
public sealed class BackupSubscriptionRequest : BackupArchiveRequest
{
    /// <summary>Aboneliğin (müşteri hesabının) kimliği.</summary>
    [Required]
    public Guid UserId { get; set; }

    public bool IncludeFiles { get; set; } = true;

    public bool IncludeDatabases { get; set; } = true;

    public bool IncludeMail { get; set; } = true;

    public bool IncludeFtp { get; set; } = true;
}
