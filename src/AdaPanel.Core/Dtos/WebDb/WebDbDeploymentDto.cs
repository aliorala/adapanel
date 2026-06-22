namespace AdaPanel.Core.Dtos.WebDb;

public sealed class WebDbDeploymentDto
{
    public string Provider { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ComponentId { get; set; } = string.Empty;

    public string SiteName { get; set; } = string.Empty;

    public int Port { get; set; }

    public string Url { get; set; } = string.Empty;

    public string InstallDir { get; set; } = string.Empty;

    public string EntryFilePath { get; set; } = string.Empty;

    public string PhpComponentId { get; set; } = string.Empty;

    public string SupportedEngines { get; set; } = string.Empty;

    public string AccessMode { get; set; } = string.Empty;

    public bool IsInstalled { get; set; }

    public bool IsDeployed { get; set; }

    public bool IsHealthy { get; set; }

    public string HealthMessage { get; set; } = string.Empty;

    public string HealthDetails { get; set; } = string.Empty;
}
