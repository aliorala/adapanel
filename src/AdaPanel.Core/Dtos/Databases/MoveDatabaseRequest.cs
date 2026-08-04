using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// Veritabanını başka bir siteye (aboneliğe) taşıma isteği.
/// <para>
/// Taşıma, MySQL tarafında hiçbir şeyi hareket ettirmez: kullanıcılar ve yetkiler olduğu gibi
/// kalır, dolayısıyla o an çalışan uygulamanın bağlantısı KOPMAZ. Değişen şey panelde kimin
/// gördüğü ve kimin kotasına sayıldığıdır.
/// </para>
/// </summary>
public sealed class MoveDatabaseRequest
{
    [Required]
    [StringLength(253, MinimumLength = 1)]
    public string TargetSiteName { get; init; } = string.Empty;
}
