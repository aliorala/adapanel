using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// Var olan (uygulama/elle oluşturulmuş) bir veritabanı + kullanıcıyı panel yönetimine "içe aktarır":
/// kullanıcıya o DB'de tam yetki verir (eksikse) ve parolasını şifreli saklar → phpMyAdmin "bu kullanıcı
/// olarak aç" çalışır ve panel yedek/şifre/yetkiyi yönetebilir. Kullanıcı zaten var olmalıdır.
/// </summary>
public sealed class ImportDatabaseRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public string Engine { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string DatabaseName { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string UserName { get; init; } = string.Empty;

    [StringLength(255)]
    public string Host { get; init; } = "%";

    [Required]
    [StringLength(128)]
    public string Password { get; init; } = string.Empty;
}
