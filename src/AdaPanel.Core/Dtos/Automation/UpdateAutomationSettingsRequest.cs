using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Automation;

public sealed class UpdateAutomationSettingsRequest
{
    public bool AutoBackupEnabled { get; set; }

    [Range(1, 24 * 30)]
    public int BackupIntervalHours { get; set; } = 24;

    [Range(0, 3650)]
    public int BackupRetentionDays { get; set; } = 14;

    public bool BackupSites { get; set; } = true;

    public bool BackupDatabases { get; set; }

    public bool BackupMail { get; set; }

    public bool BackupPanelConfig { get; set; }

    public IReadOnlyCollection<string> BackupDatabaseEngines { get; set; } = [];

    public bool AutoSslRenewEnabled { get; set; }

    [Range(1, 24 * 30)]
    public int SslRenewIntervalHours { get; set; } = 24;

    /// <summary>Otomatik yedeklerin uzak hedefi: local | ftp | s3 | webdav.</summary>
    [StringLength(16)]
    public string BackupTargetType { get; set; } = "local";

    [StringLength(2048)]
    public string BackupRemoteUrl { get; set; } = string.Empty;

    [StringLength(255)]
    public string BackupRemoteUserName { get; set; } = string.Empty;

    /// <summary>Boş bırakılırsa kayıtlı parola korunur.</summary>
    [StringLength(255)]
    public string BackupRemotePassword { get; set; } = string.Empty;

    [StringLength(64)]
    public string BackupRemoteRegion { get; set; } = string.Empty;

    public bool AlertsEnabled { get; set; }

    [Range(50, 99)]
    public int DiskUsageThresholdPercent { get; set; } = 90;

    [Range(1, 90)]
    public int CertExpiryWarningDays { get; set; } = 14;

    [Range(50, 99)]
    public int MemoryUsageThresholdPercent { get; set; } = 90;

    public bool ServiceDownAlertsEnabled { get; set; } = true;
}
