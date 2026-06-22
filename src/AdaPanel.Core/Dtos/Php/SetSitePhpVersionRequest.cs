using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Php;

public sealed class SetSitePhpVersionRequest
{
    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string ComponentId { get; init; } = string.Empty;
}
