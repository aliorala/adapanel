using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Automation;

public sealed class UpdateAutomationSettingsRequest
{
    public bool AutoBackupEnabled { get; set; }

    /// <summary>interval | daily | weekly | monthly. Tanınmayan değer <c>interval</c> sayılır.</summary>
    [StringLength(16)]
    public string BackupScheduleMode { get; set; } = "interval";

    [Range(1, 24 * 30)]
    public int BackupIntervalHours { get; set; } = 24;

    /// <summary>Takvim modlarında çalışma saati (SUNUCU YEREL saati).</summary>
    [Range(0, 23)]
    public int BackupScheduleHour { get; set; } = 3;

    [Range(0, 59)]
    public int BackupScheduleMinute { get; set; }

    /// <summary>0 = Pazar … 6 = Cumartesi.</summary>
    [Range(0, 6)]
    public int BackupScheduleDayOfWeek { get; set; } = 1;

    [Range(1, 31)]
    public int BackupScheduleDayOfMonth { get; set; } = 1;

    /// <summary>İsteğe bağlı ikinci günlük saat; null = kapalı. Sunucunun yerel saati.</summary>
    [Range(0, 23)]
    public int? BackupScheduleSecondHour { get; set; }

    [Range(0, 59)]
    public int BackupScheduleSecondMinute { get; set; }

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

    /// <summary>IIS site erişim loglarının otomatik rotasyonu (günde bir; eski loglar silinir).</summary>
    public bool LogCleanupEnabled { get; set; }

    [Range(1, 3650)]
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>Kurulu bileşenler için günlük güncelleme kontrolü (varsayılan açık).</summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>Bulunan güncellemeleri otomatik kur (riskli bileşenler hariç; varsayılan kapalı).</summary>
    public bool AutoApplyComponentUpdates { get; set; }
}
