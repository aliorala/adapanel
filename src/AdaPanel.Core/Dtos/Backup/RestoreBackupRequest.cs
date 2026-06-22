namespace AdaPanel.Core.Dtos.Backup;

public sealed class RestoreBackupRequest
{
    public string BackupId { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;
}
