using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Asp;

public sealed class EnableMixedHandlersRequest
{
    public bool EnableClassicAsp { get; init; } = true;

    [StringLength(32)]
    public string ManagedRuntimeVersion { get; init; } = "v4.0";

    [StringLength(32)]
    public string ManagedPipelineMode { get; init; } = "Integrated";

    public bool Enable32BitAppOnWin64 { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string PhpComponentId { get; init; } = string.Empty;
}
