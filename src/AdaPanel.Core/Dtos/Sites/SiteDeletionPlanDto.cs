namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Bir site silinmeden önce, siteye bağlı tüm kaynakların (profesyonel panel benzeri cascade)
/// önizlemesi. Kullanıcı silme onayında neyin gideceğini görür.
/// </summary>
public sealed class SiteDeletionPlanDto
{
    public string SiteName { get; set; } = string.Empty;

    public string PhysicalPath { get; set; } = string.Empty;

    public string ApplicationRootPath { get; set; } = string.Empty;

    /// <summary>Panel DNS bölgesi mevcut mu?</summary>
    public bool HasDnsZone { get; set; }

    /// <summary>Siteye/bağlamalara karşılık gelen mail domainleri (varsa).</summary>
    public string[] MailDomains { get; set; } = [];

    /// <summary>Siteye ait FTP kullanıcıları.</summary>
    public string[] FtpUsers { get; set; } = [];

    /// <summary>Adı siteyle eşleşen veritabanları (motor:ad).</summary>
    public string[] Databases { get; set; } = [];

    public bool HasReverseProxy { get; set; }

    public bool HasDiskQuota { get; set; }

    public bool HasGitDeployment { get; set; }

    /// <summary>Bu siteye atanmış müşteri sayısı (atama temizlenecek).</summary>
    public int CustomerAssignments { get; set; }

    /// <summary>
    /// Sitenin KLASÖRÜ İÇİNDEN çalışan zamanlanmış görev sayısı (kaldırılacak).
    /// <para>
    /// Onayda ayrıca gösterilir çünkü kullanıcının en çok şaşıracağı kalem budur: bunlar site
    /// silinse de tetiklenmeye devam eden gerçek Windows görevleridir ve panelde artık hangi
    /// siteye ait olduklarını gösteren bir şey kalmaz.
    /// </para>
    /// </summary>
    public int ScheduledTasks { get; set; }
}

/// <summary>Cascade silme sonucu: silinenler + bloke olmayan uyarılar.</summary>
public sealed class SiteDeletionResultDto
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Başarıyla silinen kaynakların kullanıcıya gösterilebilir özeti.</summary>
    public List<string> Removed { get; set; } = [];

    /// <summary>Silinemeyen/atlanan kaynaklar (best-effort; ana silmeyi bloke etmez).</summary>
    public List<string> Warnings { get; set; } = [];
}
