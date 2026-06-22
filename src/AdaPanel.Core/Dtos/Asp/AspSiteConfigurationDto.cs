namespace AdaPanel.Core.Dtos.Asp;

public sealed class AspSiteConfigurationDto
{
    public string SiteName { get; init; } = string.Empty;

    public string ApplicationPoolName { get; init; } = string.Empty;

    public bool ClassicAspEnabled { get; init; }

    public string ManagedRuntimeVersion { get; init; } = string.Empty;

    public string ManagedPipelineMode { get; init; } = "Integrated";

    public bool Enable32BitAppOnWin64 { get; init; }

    public bool HasManagedRuntime => !string.IsNullOrWhiteSpace(ManagedRuntimeVersion);

    public bool HasPhpHandler { get; init; }

    public string PhpComponentId { get; init; } = string.Empty;

    public string PhpHandlerName { get; init; } = string.Empty;

    public bool IsMixedHandlersEnabled { get; init; }
}
