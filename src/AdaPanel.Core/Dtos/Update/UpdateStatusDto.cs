namespace AdaPanel.Core.Dtos.Update;

public sealed class UpdateStatusDto
{
    public string CurrentVersion { get; set; } = string.Empty;

    public bool IsUpdateAvailable { get; set; }

    public string LatestVersion { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public string PackageSha256 { get; set; } = string.Empty;

    public string PackagePath { get; set; } = string.Empty;

    public bool IsPackageReady { get; set; }

    public bool IsInstallScheduled { get; set; }

    public DateTime? InstallScheduledAtUtc { get; set; }

    public string Notes { get; set; } = string.Empty;

    /// <summary>GitHub deposu (owner/repo). Panel en son sürümü GitHub Releases'ten otomatik görür. Boş = ayarlanmamış.</summary>
    public string GithubRepo { get; set; } = string.Empty;

    public DateTime CheckedAtUtc { get; set; }
}
