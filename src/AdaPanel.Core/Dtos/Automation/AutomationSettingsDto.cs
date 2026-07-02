namespace AdaPanel.Core.Dtos.Automation;

/// <summary>
/// Panel içi arka plan zamanlayıcısının kalıcı ayarları.
/// Otomatik yedekleme ve otomatik SSL yenileme job'larını yönetir.
/// </summary>
public sealed class AutomationSettingsDto
{
    public bool AutoBackupEnabled { get; set; }

    /// <summary>Yedekleme job'unun tekrar aralığı (saat). Minimum 1.</summary>
    public int BackupIntervalHours { get; set; } = 24;

    /// <summary>Bu günden eski yerel yedekler silinir. 0 = sınırsız (silme).</summary>
    public int BackupRetentionDays { get; set; } = 14;

    /// <summary>Site dosya yedeklerini al.</summary>
    public bool BackupSites { get; set; } = true;

    /// <summary>Veritabanı dökümlerini de al.</summary>
    public bool BackupDatabases { get; set; }

    /// <summary>Mail (Stalwart) verisini de yedekle (servis kısa süre durdurulur).</summary>
    public bool BackupMail { get; set; }

    /// <summary>Panelin kendi yapılandırmasını (adapanel.db + config) da yedekle. Anahtarlar hariç.</summary>
    public bool BackupPanelConfig { get; set; }

    /// <summary>Yedeklenecek veritabanı motorları (mysql, postgresql, mssql). Boşsa varsayılan mysql.</summary>
    public IReadOnlyCollection<string> BackupDatabaseEngines { get; set; } = [];

    public bool AutoSslRenewEnabled { get; set; }

    /// <summary>SSL yenileme kontrolü aralığı (saat). Minimum 1.</summary>
    public int SslRenewIntervalHours { get; set; } = 24;

    /// <summary>Otomatik yedeklerin uzak hedefi: local | ftp | s3 | webdav.</summary>
    public string BackupTargetType { get; set; } = "local";

    public string BackupRemoteUrl { get; set; } = string.Empty;

    public string BackupRemoteUserName { get; set; } = string.Empty;

    /// <summary>S3 bölgesi (örn. eu-central-1).</summary>
    public string BackupRemoteRegion { get; set; } = string.Empty;

    /// <summary>Parola/gizli anahtar kayıtlı mı (değer asla geri dönmez).</summary>
    public bool BackupRemotePasswordSet { get; set; }

    /// <summary>Disk/sertifika uyarı bildirimleri açık mı.</summary>
    public bool AlertsEnabled { get; set; }

    /// <summary>Disk doluluk yüzdesi bu eşiği aşınca uyarı gönderilir.</summary>
    public int DiskUsageThresholdPercent { get; set; } = 90;

    /// <summary>Sertifika bitişine bu kadar gün kalınca uyarı gönderilir.</summary>
    public int CertExpiryWarningDays { get; set; } = 14;

    /// <summary>RAM (bellek) doluluk yüzdesi bu eşiği aşınca uyarı gönderilir.</summary>
    public int MemoryUsageThresholdPercent { get; set; } = 90;

    /// <summary>Kurulu bir servis (web/mail/DB/FTP) durduğunda uyarı gönderilsin mi.</summary>
    public bool ServiceDownAlertsEnabled { get; set; } = true;
}
