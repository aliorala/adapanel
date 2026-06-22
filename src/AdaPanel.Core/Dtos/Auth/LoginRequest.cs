using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Auth;

public sealed class LoginRequest
{
    [Required]
    [StringLength(256, MinimumLength = 3)]
    public string UserNameOrEmail { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;

    [RegularExpression("^[0-9]{6,16}$")]
    public string? TwoFactorCode { get; init; }

    public bool RememberMe { get; init; }
}
