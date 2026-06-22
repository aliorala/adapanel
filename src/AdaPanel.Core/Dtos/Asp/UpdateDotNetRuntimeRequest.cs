using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Asp;

public sealed class UpdateDotNetRuntimeRequest
{
    [StringLength(32)]
    public string ManagedRuntimeVersion { get; init; } = "v4.0";

    [StringLength(32)]
    public string ManagedPipelineMode { get; init; } = "Integrated";

    public bool Enable32BitAppOnWin64 { get; init; }
}
