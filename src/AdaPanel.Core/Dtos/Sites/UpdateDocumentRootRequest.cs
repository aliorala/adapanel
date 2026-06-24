using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Sitenin belge kökü / web root'u: IIS'in gerçekten servis ettiği fiziksel yol. Laravel/Symfony gibi
/// framework'lerde proje kökü değil <c>...\public</c> olmalıdır (aksi halde kök = 403). Panel bu değeri
/// IIS'e yazar ve kalıcı kılar (sonraki düzenlemelerde proje köküne geri DÖNMEZ).
/// </summary>
public sealed class UpdateDocumentRootRequest
{
    [Required]
    [StringLength(512, MinimumLength = 2)]
    public string PhysicalPath { get; init; } = string.Empty;
}
