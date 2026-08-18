namespace AdaPanel.Core.Dtos.Automation;

/// <summary>
/// Panel içi arka plan zamanlayıcısının kalıcı ayarları.
/// Otomatik yedekleme ve otomatik SSL yenileme job'larını yönetir.
/// </summary>
public sealed class AutomationSettingsDto
{
    public bool AutoBackupEnabled { get; set; }

    /// <summary>
    /// Yedeğin ne zaman çalışacağı: <c>interval</c> | <c>daily</c> | <c>weekly</c> | <c>monthly</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plesk'teki tek sıklık seçicinin karşılığı. <c>interval</c> eski davranıştır (son çalışmadan
    /// N saat sonra) ve VARSAYILANDIR: alan hiç yazılmamış eski kurulumlar buraya düşer, davranış
    /// değişmez. Takvim modlarında çalışma anı <see cref="BackupScheduleHour"/> ve
    /// <see cref="BackupScheduleMinute"/> ile SABİTLENİR; artık elle alınan bir yedek programın
    /// saatini kaydırmaz.
    /// </para>
    /// </remarks>
    public string BackupScheduleMode { get; set; } = "interval";

    /// <summary>Yedekleme job'unun tekrar aralığı (saat). Yalnızca <c>interval</c> modunda kullanılır. Minimum 1.</summary>
    public int BackupIntervalHours { get; set; } = 24;

    /// <summary>
    /// Takvim modlarında çalışma saati (0-23). <b>SUNUCUNUN YEREL SAATİ</b> — UTC değil, ofset de
    /// saklanmaz: "03:00" saat dilimi değişse de 03:00 kalır.
    /// </summary>
    public int BackupScheduleHour { get; set; } = 3;

    /// <summary>Takvim modlarında çalışma dakikası (0-59), yerel saat.</summary>
    public int BackupScheduleMinute { get; set; }

    /// <summary>Haftalık modda çalışma günü: 0 = Pazar … 6 = Cumartesi.</summary>
    public int BackupScheduleDayOfWeek { get; set; } = 1;

    /// <summary>
    /// Aylık modda ayın günü (1-31). O ay bu günü çekmiyorsa (Şubat'ta 31 gibi) ayın SON günü
    /// kullanılır; aksi hâlde program yılın yarısında sessizce hiç çalışmazdı.
    /// </summary>
    public int BackupScheduleDayOfMonth { get; set; } = 1;

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

    /// <summary>IIS site erişim loglarının otomatik rotasyonu (Plesk log rotation): günde bir eski loglar silinir.</summary>
    public bool LogCleanupEnabled { get; set; }

    /// <summary>Bu günden eski site logları silinir (minimum 1).</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>
    /// Yazılım mağazasındaki kurulu bileşenler için günlük güncelleme kontrolü. Açıkken panel
    /// her gün üstteki sürümleri sorgular; güncelleme çıkanlar Danışman ekranında ve (uyarılar
    /// açıksa) e-posta/webhook bildiriminde listelenir.
    /// </summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>
    /// Bulunan bileşen güncellemelerini panel kendisi kursun mu. KAPALIYKEN (varsayılan) yalnızca
    /// haber verir, kararı yöneticiye bırakır. Açıkken bile veri/kesinti riski taşıyan bileşenler
    /// (veritabanı motorları, mail sunucusu, Docker Engine) otomatik güncellenmez.
    /// </summary>
    public bool AutoApplyComponentUpdates { get; set; }
}
