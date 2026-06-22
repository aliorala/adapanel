namespace AdaPanel.Core.Dtos.Php;

public sealed class PhpVersionDto
{
    public string ComponentId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string InstallDir { get; init; } = string.Empty;

    public string ExecutablePath { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public bool IsFastCgiRegistered { get; init; }

    public DateTime InstalledAtUtc { get; init; }
}
