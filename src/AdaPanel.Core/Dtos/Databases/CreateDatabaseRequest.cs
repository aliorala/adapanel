using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Databases;

public sealed class CreateDatabaseRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public string Engine { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    // ---- profesyonel paneller gibi: veritabaniyla birlikte kullanici olustur + otomatik TAM yetki (opsiyonel) ----

    /// <summary>Boş değilse bu kullanıcı oluşturulur ve veritabanına otomatik tam yetki alır.</summary>
    [StringLength(64)]
    public string UserName { get; init; } = string.Empty;

    [StringLength(128)]
    public string Password { get; init; } = string.Empty;

    /// <summary>Boş değilse bu veritabanı siteye BAĞLANIR → site silinince bu DB de cascade ile gider (Plesk gibi).</summary>
    [StringLength(255)]
    public string SiteName { get; init; } = string.Empty;
}
