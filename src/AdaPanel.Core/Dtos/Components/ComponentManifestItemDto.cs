namespace AdaPanel.Core.Dtos.Components;

public sealed class ComponentManifestItemDto
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string InstallDir { get; init; } = string.Empty;

    public string[] PostInstall { get; init; } = [];

    public string[] Dependencies { get; init; } = [];

    public string ServiceName { get; init; } = string.Empty;

    public string SilentArgs { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    public bool Default { get; init; }
}
