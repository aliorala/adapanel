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

    [StringLength(255)]
    public string AllowedHost { get; init; } = "%";

    /// <summary>
    /// İsteğe bağlı: kullanıcı bu veritabanına OTOMATİK tam yetki (GRANT ALL) alır (Plesk gibi). Boşsa yalnız
    /// kullanıcı oluşturulur (yetki verilmez) → uygulama "Access denied to database" almasın diye genelde dolu gelir.
    /// </summary>
    [StringLength(64)]
    public string DatabaseName { get; init; } = string.Empty;
}
