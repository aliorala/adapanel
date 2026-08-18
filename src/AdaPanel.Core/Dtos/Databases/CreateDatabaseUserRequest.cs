using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Databases;

public sealed class CreateDatabaseUserRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public string Engine { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 2)]
    public string UserName { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 8)]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Hesabın hangi adresten geçerli olacağı. BOŞ BIRAKIN: panel güvenli (yerel) varsayılanı uygular.
    /// </summary>
    /// <remarks>
    /// Varsayılan eskiden <c>%</c> idi: oluşturulan her hesap dünyanın her yerinden geçerliydi ve
    /// tek engel güvenlik duvarıydı. Uzak erişim artık ayrı, açık onaylı bir karardır
    /// (<c>/db/remote-access/users</c>).
    /// </remarks>
    [StringLength(255)]
    public string AllowedHost { get; init; } = string.Empty;

    /// <summary>
    /// İsteğe bağlı: kullanıcı bu veritabanına OTOMATİK tam yetki (GRANT ALL) alır (Plesk gibi). Boşsa yalnız
    /// kullanıcı oluşturulur (yetki verilmez) → uygulama "Access denied to database" almasın diye genelde dolu gelir.
    /// </summary>
    [StringLength(64)]
    public string DatabaseName { get; init; } = string.Empty;
}
