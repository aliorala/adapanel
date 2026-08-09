using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Licensing;

/// <summary>Lisans anahtarını panele tanıtma isteği.</summary>
public sealed class LicenseActivateRequest
{
    [Required]
    [StringLength(200, MinimumLength = 8)]
    public string LicenseKey { get; init; } = string.Empty;
}
