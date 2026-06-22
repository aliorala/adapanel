using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Security;

public sealed class UpdateSecuritySettingsRequest
{
    [Required]
    [RegularExpression("^[a-z0-9-]{3,64}$")]
    public string SecureEntryPath { get; init; } = string.Empty;

    public string[] AllowedIpAddresses { get; init; } = [];
}

