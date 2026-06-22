using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Auth;

public sealed class VerifyTwoFactorRequest
{
    [Required]
    [StringLength(16, MinimumLength = 6)]
    public string Code { get; init; } = string.Empty;
}

