using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Components;

public sealed class ComponentInstallRequest
{
    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string ComponentId { get; init; } = string.Empty;
}

