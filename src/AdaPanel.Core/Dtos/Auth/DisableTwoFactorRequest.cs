using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Auth;

public sealed class DisableTwoFactorRequest
{
    [Required]
    [StringLength(128, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;
}

