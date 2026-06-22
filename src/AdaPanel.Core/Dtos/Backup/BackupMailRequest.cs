namespace AdaPanel.Core.Dtos.Backup;

public sealed class BackupMailRequest
{
    public string TargetType { get; set; } = "local";

    public string RemoteUrl { get; set; } = string.Empty;

    public string RemoteUserName { get; set; } = string.Empty;

    public string RemotePassword { get; set; } = string.Empty;

    /// <summary>S3 için bölge (örn. eu-central-1). Boşsa us-east-1 varsayılır.</summary>
    public string RemoteRegion { get; set; } = string.Empty;
}
