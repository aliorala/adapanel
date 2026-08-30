using System.ComponentModel.DataAnnotations;
using AdaPanel.Core.Common;

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
    /// <remarks>
    /// Tavan MOTORUN sınırı değil, desteklenen en geniş motorunki
    /// (<see cref="DatabaseIdentifierLimits"/>): DTO doğrulaması hangi motorun seçildiğini bilmez.
    /// Asıl sınır sunucuda, motor bilindikten sonra uygulanır (<c>database.user_name_too_long</c>).
    /// </remarks>
    [StringLength(DatabaseIdentifierLimits.RequestCeiling)]
    public string UserName { get; init; } = string.Empty;

    [StringLength(128)]
    public string Password { get; init; } = string.Empty;

    /// <summary>Boş değilse bu veritabanı siteye BAĞLANIR → site silinince bu DB de cascade ile gider (Plesk gibi).</summary>
    [StringLength(255)]
    public string SiteName { get; init; } = string.Empty;
}
