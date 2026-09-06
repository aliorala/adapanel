using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Migration;

/// <summary>
/// Tek akışta site göçü (Plesk/cPanel/ham sunucu → AdaPanel): siteyi oluştur + dosyaları FTP/SFTP ile çek +
/// veritabanını mysqldump→mysql ile aktar. Her adım bağımsız (biri başarısız olsa diğerleri yürür); sonuç
/// adım-adım raporlanır. Mevcut, test edilmiş parçaları (site sağlama, dosya çekme, DB transfer) orkestre eder.
/// </summary>
public sealed class MigrateSiteRequest
{
    // ---- Hedef site ----
    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string SiteName { get; init; } = string.Empty;

    /// <summary>Siteyi panelde oluştur (mevcutsa false bırak — dosyalar/DB var olan siteye gider).</summary>
    public bool CreateSite { get; init; } = true;

    [StringLength(1024)]
    public string PhysicalPath { get; init; } = string.Empty;

    // ---- Dosya kaynağı (opsiyonel) ----
    public bool PullFiles { get; init; }

    /// <summary>ftp | ftps | sftp</summary>
    [StringLength(8)]
    public string FileProtocol { get; init; } = "sftp";

    [StringLength(255)]
    public string FileHost { get; init; } = string.Empty;

    public int FilePort { get; init; }

    [StringLength(128)]
    public string FileUserName { get; init; } = string.Empty;

    [StringLength(256)]
    public string FilePassword { get; init; } = string.Empty;

    /// <summary>Kaynak belge kökü (örn. /httpdocs, /public_html).</summary>
    [StringLength(1024)]
    public string RemotePath { get; init; } = "/";

    /// <summary>
    /// Yöneticinin BİLEREK kabul ettiği dosya kaynağı parmak izi (FTPS sertifikası ya da SFTP host key).
    /// Boşsa doğrulama uygulanır; gerekçe
    /// <see cref="Sites.RemoteFileMigrationRequest.AcceptHostFingerprint"/>'te.
    /// </summary>
    [StringLength(128)]
    public string AcceptFileHostFingerprint { get; init; } = string.Empty;

    // ---- Veritabanı kaynağı (opsiyonel) ----
    public bool TransferDatabase { get; init; }

    [StringLength(32)]
    public string DatabaseEngine { get; init; } = "mariadb";

    [StringLength(255)]
    public string DbHost { get; init; } = string.Empty;

    public int DbPort { get; init; } = 3306;

    [StringLength(128)]
    public string DbUserName { get; init; } = string.Empty;

    [StringLength(256)]
    public string DbPassword { get; init; } = string.Empty;

    [StringLength(64)]
    public string SourceDatabase { get; init; } = string.Empty;

    /// <summary>
    /// Kaynak veritabanına bağlanırken şifreleme: <c>auto</c> | <c>required</c> | <c>disabled</c>.
    /// Gerekçe ve varsayılanlar <c>DatabaseTransferRequest.SourceTlsMode</c>'da.
    /// </summary>
    [StringLength(16)]
    public string DbTlsMode { get; init; } = string.Empty;

    /// <summary>Hedef DB adı (boşsa kaynak adı).</summary>
    [StringLength(64)]
    public string TargetDatabase { get; init; } = string.Empty;

    /// <summary>Taşınan uygulamanın bağlanacağı hedef DB KULLANICISI (wp-config.php/.env'deki). Boşsa site adından
    /// türetilir. Bu kullanıcı yeni sunucuda oluşturulup hedef DB'ye tam yetki alır (Plesk göçü gibi).</summary>
    [StringLength(64)]
    public string TargetDatabaseUser { get; init; } = string.Empty;

    /// <summary>Hedef DB kullanıcısının parolası. Boşsa güçlü bir parola üretilir ve sonuç adımında gösterilir.</summary>
    [StringLength(128)]
    public string TargetDatabasePassword { get; init; } = string.Empty;
}
