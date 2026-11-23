using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Auth;

/// <summary>İkinci adım (2FA) kurulum/kurtarma kodu işlemlerinde MEVCUT parola kanıtı (denetim G12).</summary>
public sealed class TwoFactorPasswordRequest
{
    [Required]
    [StringLength(128)]
    public string Password { get; init; } = string.Empty;
}
