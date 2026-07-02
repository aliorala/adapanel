namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Budama (retention) sırasında off-site (S3/FTP/WebDAV) yedeklerin UZAK kopyasını da silmek için
/// gereken kimlik bilgileri. Yedek kaydındaki RemoteUri temizlenmiş (kimliksiz) olduğundan, silme
/// için güncel hedef kimlikleri buradan gelir. Boşsa yalnız yerel kopya budanır (uzak korunur).
/// </summary>
public sealed class BackupPruneRemoteOptions
{
    public string UserName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;
}
