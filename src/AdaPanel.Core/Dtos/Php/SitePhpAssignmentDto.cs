namespace AdaPanel.Core.Dtos.Php;

public sealed class SitePhpAssignmentDto
{
    public string SiteName { get; init; } = string.Empty;

    public string ComponentId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string ExecutablePath { get; init; } = string.Empty;

    public string HandlerName { get; init; } = string.Empty;

    public bool IsFastCgiRegistered { get; init; }
}
