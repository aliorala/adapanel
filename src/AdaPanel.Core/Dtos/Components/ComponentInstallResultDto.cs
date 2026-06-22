namespace AdaPanel.Core.Dtos.Components;

public sealed class ComponentInstallResultDto
{
    public string ComponentId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string InstallDir { get; init; } = string.Empty;

    public string ServiceName { get; init; } = string.Empty;

    public string GeneratedPassword { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

