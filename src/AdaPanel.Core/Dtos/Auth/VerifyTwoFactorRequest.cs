using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Auth;

public sealed class VerifyTwoFactorRequest
{
    [Required]
    [StringLength(16, MinimumLength = 6)]
    public string Code { get; init; } = string.Empty;

    /// <summary>Mevcut parola (G12): kurulumu onaylamak için çerez tek başına yetmez.</summary>
    [StringLength(128)]
    public string Password { get; init; } = string.Empty;
}

