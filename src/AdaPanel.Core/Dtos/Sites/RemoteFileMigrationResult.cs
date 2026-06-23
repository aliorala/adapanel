namespace AdaPanel.Core.Dtos.Sites;

public sealed class RemoteFileMigrationResult
{
    public string SiteName { get; set; } = string.Empty;

    public string Protocol { get; set; } = string.Empty;

    public int FilesDownloaded { get; set; }

    public long BytesDownloaded { get; set; }

    public string TargetPath { get; set; } = string.Empty;
}
