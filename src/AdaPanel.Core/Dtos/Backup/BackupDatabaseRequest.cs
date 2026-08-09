namespace AdaPanel.Core.Dtos.Backup;

public sealed class BackupDatabaseRequest : BackupTargetRequest
{
    public string Engine { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = string.Empty;
}
