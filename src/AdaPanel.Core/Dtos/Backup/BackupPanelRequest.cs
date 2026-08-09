namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Panelin KENDİ durumunu yedekleme isteği: adapanel.db + config/*.json.
/// DataProtection anahtarları ve sertifikalar BİLEREK dışarıdadır (makineye bağlı/hassas).
/// </summary>
public sealed class BackupPanelRequest : BackupTargetRequest;
