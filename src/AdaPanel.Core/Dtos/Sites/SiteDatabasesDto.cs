using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Sites;

/// <summary>Bir veritabanına işaret (motor + ad).</summary>
public sealed class SiteDatabaseRefDto
{
    public string Engine { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Bir sitenin veritabanı görünümü: siteye BAĞLI olanlar (açık seçimle bağlanan + adı eşleşen) ve bağlanabilecek
/// ADAYLAR (henüz bağlı olmayan mevcut veritabanları). Kullanıcı isim tahminine bağlı kalmadan seçerek bağlar (Plesk gibi).
/// </summary>
public sealed class SiteDatabasesDto
{
    public List<SiteDatabaseRefDto> Linked { get; set; } = [];

    public List<SiteDatabaseRefDto> Candidates { get; set; } = [];
}

/// <summary>Mevcut bir veritabanını siteye bağlama isteği (seçerek).</summary>
public sealed class LinkSiteDatabaseRequest
{
    [Required]
    [StringLength(32, MinimumLength = 2)]
    public string Engine { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;
}
