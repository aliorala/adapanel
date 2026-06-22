using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Setup;

public sealed class InitialSetupRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public string UserName { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 12)]
    public string ConfirmPassword { get; init; } = string.Empty;

    [Required]
    [RegularExpression("^[a-z0-9-]{3,64}$")]
    public string SecureEntryPath { get; init; } = "panel";

    [Required]
    [RegularExpression("^(tr|en)(-[A-Z]{2})?$")]
    public string PreferredLanguage { get; init; } = "tr";

    [Range(1, 65535)]
    public int PreferredHttpsPort { get; init; } = 8888;

    public bool InstallRecommendedComponents { get; init; } = true;
}
