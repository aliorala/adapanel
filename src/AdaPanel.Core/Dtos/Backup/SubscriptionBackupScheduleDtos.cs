using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// TEK bir aboneliğin kendi yedek programı (Plesk'te her aboneliğin kendi "Backup Schedule"ı vardır).
/// </summary>
/// <remarks>
/// <para>
/// Panelde önceden yalnızca SUNUCU GENELİ tek bir program vardı: bütün müşteriler aynı saatte,
/// aynı sıklıkta ve aynı saklama süresiyle yedekleniyordu. Bu, aboneliği bir ürün olarak
/// satmayı imkânsız kılar — "günde 2 kez yedek" ya da "30 gün saklama" satılabilir bir farktır.
/// </para>
/// <para>
/// Sunucu geneli program KALDIRILMADI: panelin kendi durumu, sahipsiz veritabanları ve posta
/// sunucusu hâlâ oradan yedeklenir. Abonelik programı onun YERİNE değil ÜSTÜNE gelir.
/// </para>
/// </remarks>
public sealed class SubscriptionBackupScheduleDto
{
    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>
    /// <c>interval</c> | <c>daily</c> | <c>weekly</c> | <c>monthly</c>. Sunucu geneli programla
    /// AYNI kavram ve aynı kod (bkz. <c>BackupSchedule</c>); varsayılan <c>interval</c> olduğu
    /// için mevcut abonelik programları hiç değişmeden çalışmaya devam eder.
    /// </summary>
    public string ScheduleMode { get; set; } = "interval";

    /// <summary>Kaç saatte bir yedek alınacak (1 = saatlik, 24 = günlük). Yalnızca <c>interval</c> modunda.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>Takvim modlarında çalışma saati (0-23) — SUNUCUNUN YEREL saati.</summary>
    public int ScheduleHour { get; set; } = 3;

    /// <summary>Takvim modlarında çalışma dakikası (0-59).</summary>
    public int ScheduleMinute { get; set; }

    /// <summary>Haftalık modda gün: 0 = Pazar … 6 = Cumartesi.</summary>
    public int ScheduleDayOfWeek { get; set; } = 1;

    /// <summary>Aylık modda ayın günü (1-31); ay o günü çekmiyorsa ayın son günü kullanılır.</summary>
    public int ScheduleDayOfMonth { get; set; } = 1;

    /// <summary>İsteğe bağlı ikinci günlük saat; null = kapalı. Sunucunun yerel saati.</summary>
    [Range(0, 23)]
    public int? ScheduleSecondHour { get; set; }

    [Range(0, 59)]
    public int ScheduleSecondMinute { get; set; }

    /// <summary>Saklama süresi, gün (0 = süreye göre silme yok).</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>
    /// En fazla kaç yedek SAKLANIR (0 = sınırsız). Süre kadar sayı da bir limittir: günde iki
    /// yedek alan bir abonelik 14 günde 28 arşiv bırakır.
    /// </summary>
    public int KeepCount { get; set; }

    public bool Incremental { get; set; }

    public int FullBackupEveryDays { get; set; } = 7;

    public bool IncludeFiles { get; set; } = true;

    public bool IncludeDatabases { get; set; } = true;

    public bool IncludeMail { get; set; } = true;

    public bool IncludeFtp { get; set; } = true;

    /// <summary>"local" | "ftp" | "s3" | "webdav".</summary>
    public string TargetType { get; set; } = "local";

    public string RemoteUrl { get; set; } = string.Empty;

    public string RemoteUserName { get; set; } = string.Empty;

    public string RemoteRegion { get; set; } = string.Empty;

    /// <summary>Uzak hedef parolası KAYITLI mı (parolanın kendisi ASLA dönmez).</summary>
    public bool RemotePasswordSet { get; set; }

    public DateTime? LastRunUtc { get; set; }

    /// <summary>"completed" | "completed_with_errors" | "failed" | "" (hiç çalışmadı).</summary>
    public string LastStatus { get; set; } = string.Empty;

    public string LastDetails { get; set; } = string.Empty;

    public DateTime? NextRunUtc { get; set; }

    // --- Plandan çözülen kota (salt okunur) ---

    public string PlanId { get; set; } = string.Empty;

    public string PlanName { get; set; } = string.Empty;

    /// <summary>Planın günlük yedek sayısı sınırı (0 = sınırsız).</summary>
    public int MaxBackupsPerDay { get; set; }

    /// <summary>
    /// Kaydedilen aralık kotaya TAKILDI mı: plan daraltıldığında program kendiliğinden kotaya
    /// çekilir ve bu bayrak ekranda "planınız günde N yedeğe izin veriyor" demeyi mümkün kılar.
    /// </summary>
    public bool ThrottledByQuota { get; set; }
}

/// <summary>Abonelik yedek programını güncelleme isteği.</summary>
public sealed class UpdateSubscriptionBackupScheduleRequest
{
    public bool Enabled { get; set; }

    /// <summary>interval | daily | weekly | monthly. Tanınmayan değer <c>interval</c> sayılır.</summary>
    [StringLength(16)]
    public string ScheduleMode { get; set; } = "interval";

    [Range(1, 24 * 30)]
    public int IntervalHours { get; set; } = 24;

    /// <summary>Takvim modlarında çalışma saati (SUNUCU YEREL saati).</summary>
    [Range(0, 23)]
    public int ScheduleHour { get; set; } = 3;

    [Range(0, 59)]
    public int ScheduleMinute { get; set; }

    /// <summary>0 = Pazar … 6 = Cumartesi.</summary>
    [Range(0, 6)]
    public int ScheduleDayOfWeek { get; set; } = 1;

    [Range(1, 31)]
    public int ScheduleDayOfMonth { get; set; } = 1;

    /// <summary>İsteğe bağlı ikinci günlük saat; null = kapalı. Sunucunun yerel saati.</summary>
    [Range(0, 23)]
    public int? ScheduleSecondHour { get; set; }

    [Range(0, 59)]
    public int ScheduleSecondMinute { get; set; }

    [Range(0, 3650)]
    public int RetentionDays { get; set; } = 14;

    [Range(0, 1000)]
    public int KeepCount { get; set; }

    public bool Incremental { get; set; }

    [Range(1, 365)]
    public int FullBackupEveryDays { get; set; } = 7;

    public bool IncludeFiles { get; set; } = true;

    public bool IncludeDatabases { get; set; } = true;

    public bool IncludeMail { get; set; } = true;

    public bool IncludeFtp { get; set; } = true;

    public string TargetType { get; set; } = "local";

    public string RemoteUrl { get; set; } = string.Empty;

    public string RemoteUserName { get; set; } = string.Empty;

    /// <summary>Boş gönderilirse KAYITLI parola korunur (ekran parolayı geri okuyamaz).</summary>
    public string RemotePassword { get; set; } = string.Empty;

    public string RemoteRegion { get; set; } = string.Empty;
}
