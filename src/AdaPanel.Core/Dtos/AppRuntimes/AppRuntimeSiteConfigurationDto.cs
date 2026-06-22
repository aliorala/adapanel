namespace AdaPanel.Core.Dtos.AppRuntimes;

public sealed class AppRuntimeSiteConfigurationDto
{
    public string SiteName { get; init; } = string.Empty;

    public string RuntimeType { get; init; } = "none";

    public string ComponentId { get; init; } = string.Empty;

    public string ProcessPath { get; init; } = string.Empty;

    public string StartupFile { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = string.Empty;

    public string Arguments { get; init; } = string.Empty;

    public string WebConfigPath { get; init; } = string.Empty;

    public string LogsDirectory { get; init; } = string.Empty;

    public bool IsEnabled { get; init; }

    public bool HasHttpPlatformHandler { get; init; }

    public bool ManagedRuntimeDisabled { get; init; }

    public string[] EnvironmentVariables { get; init; } = [];
}
