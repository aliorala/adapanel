using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Sites;

public sealed class CreateSiteRequest
{
    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [StringLength(1024, MinimumLength = 3)]
    // set: SitesController non-admin (bayi/müşteri) için yolu güvenli köke ZORLAR (çapraz-kiracı yolu engellenir).
    public string PhysicalPath { get; set; } = string.Empty;

    [StringLength(128)]
    public string ApplicationPoolName { get; init; } = string.Empty;

    [StringLength(255)]
    public string HostName { get; init; } = string.Empty;

    [StringLength(64)]
    public string IpAddress { get; init; } = "*";

    [Range(1, 65535)]
    public int Port { get; init; } = 80;

    [Required]
    [RegularExpression("^(http|https)$")]
    public string Protocol { get; init; } = "http";

    public bool AutoStart { get; init; } = true;

    [Required]
    [RegularExpression("^(auto|static|php|aspnet|classic-asp|mixed|node|python)$")]
    public string RuntimeProfile { get; init; } = "auto";

    [StringLength(128)]
    public string RuntimeComponentId { get; init; } = string.Empty;

    public bool InstallRuntimeIfMissing { get; init; } = true;

    [StringLength(260)]
    public string StartupFile { get; init; } = string.Empty;

    // ---- profesyonel panel benzeri birleşik oluşturma: site ile birlikte opsiyonel kaynaklar ----

    /// <summary>
    /// Kurulu servisleri algılayıp veritabanı, mail domaini ve FTP hesabı gibi
    /// standart hosting kaynaklarını kullanıcıdan teknik seçim istemeden hazırlar.
    /// </summary>
    public bool ProvisionDefaultServices { get; init; } = true;

    /// <summary>Site ile birlikte bir veritabanı da oluştur.</summary>
    public bool CreateDatabase { get; init; }

    [StringLength(32)]
    public string DatabaseEngine { get; init; } = string.Empty;

    /// <summary>Boşsa site adından türetilir.</summary>
    [StringLength(64)]
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>Veritabanı kullanıcısı (boşsa site adından türetilir).</summary>
    [StringLength(64)]
    public string DatabaseUserName { get; init; } = string.Empty;

    /// <summary>Veritabanı kullanıcı parolası (boşsa otomatik üretilir).</summary>
    [StringLength(256)]
    public string DatabasePassword { get; init; } = string.Empty;

    /// <summary>Site ile birlikte bir mail domaini de oluştur (host adı ya da site adı).</summary>
    public bool CreateMailDomain { get; init; }

    /// <summary>Site ile birlikte bir FTP hesabı da oluştur.</summary>
    public bool CreateFtpUser { get; init; }

    [StringLength(64)]
    public string FtpUserName { get; init; } = string.Empty;

    [StringLength(256)]
    public string FtpPassword { get; init; } = string.Empty;
}
