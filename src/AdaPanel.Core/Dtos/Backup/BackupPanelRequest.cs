namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Panelin KENDİ durumunu yedekleme isteği: adapanel.db + config/*.json.
/// DataProtection anahtarları ve sertifikalar BİLEREK dışarıdadır (makineye bağlı/hassas).
/// </summary>
public sealed class BackupPanelRequest
{
    public string TargetType { get; set; } = "local";

    public string RemoteUrl { get; set; } = string.Empty;

    public string RemoteUserName { get; set; } = string.Empty;

    public string RemotePassword { get; set; } = string.Empty;

    /// <summary>S3 için bölge (örn. eu-central-1). Boşsa us-east-1 varsayılır.</summary>
    public string RemoteRegion { get; set; } = string.Empty;
}
