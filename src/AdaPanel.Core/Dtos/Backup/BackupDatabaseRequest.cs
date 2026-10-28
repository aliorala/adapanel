namespace AdaPanel.Core.Dtos.Backup;

public sealed class BackupDatabaseRequest : BackupTargetRequest
{
    public string Engine { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = string.Empty;

    public bool PortableDefiners { get; set; }

    /// <summary>Yalnız yönetici DB ucunda: RemoteUrl ile onaylanan kayıtlı FTP hedefini kullanır.</summary>
    public bool UseConfiguredFtpTarget { get; set; }
}
