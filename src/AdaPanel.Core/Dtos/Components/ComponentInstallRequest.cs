using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Components;

public sealed class ComponentInstallRequest
{
    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string ComponentId { get; init; } = string.Empty;

    /// <summary>
    /// Bileşen zaten kurulu ve sağlıklıysa normalde kurulum ATLANIR (gereksiz yeniden kurulum
    /// riski). Sürüm YÜKSELTMESİ tam olarak bu atlamayı aşmak ister: "Güncelle" düğmesi bunu
    /// true gönderir. Kullanıcının bilinçli eylemi olmadan asla true olmamalıdır.
    /// </summary>
    public bool Force { get; init; }
}

