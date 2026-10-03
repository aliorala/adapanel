namespace AdaPanel.Core.Dtos.AppRuntimes;

/// <summary>Only runtime availability; never executable paths, credentials or installation metadata.</summary>
public sealed class AppRuntimeInventoryDto
{
    public string ComponentId { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }
}
