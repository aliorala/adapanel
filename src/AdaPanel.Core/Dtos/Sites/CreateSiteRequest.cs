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

    // set: SitesController admin OLMAYAN istekte bunu TEMİZLER — havuz seçimi kiracıya bırakılamaz
    // (aşağıdaki gerekçe orada yazılı). Boş bırakıldığında panel yönetilen adı kendisi üretir.
    [StringLength(128)]
    public string ApplicationPoolName { get; set; } = string.Empty;

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

    /// <summary>
    /// Aynı adda bir IIS sitesi ZATEN VARSA onu kaldırıp yeniden oluşturmaya AÇIK onay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>CANLIDA ÖLÇÜLEN TEHLİKE.</b> <c>WebAdministrationSiteService.CreateSiteAsync</c> aynı adlı
    /// bir site bulunca onu KALDIRIP yeniden oluşturur. Bu davranış bilinçliydi (önceki bir AdaPanel
    /// kurulumundan kalan artık siteyi devralmak için) ama hiçbir onay istemiyordu ve yönetici
    /// <c>EnsureSiteNameIsClaimableAsync</c> denetiminin TAMAMEN dışındaydı.
    /// </para>
    /// <para>
    /// Sonuç: "Oluştur ve yayınla" düğmesine yanlışlıkla ikinci kez basmak, az önce kurulan siteyi
    /// SİLİP yeniden kuruyordu — ve yan kaynaklar (veritabanı, FTP kullanıcısı, posta alanı) yeniden
    /// sağlanıyordu. Kullanıcı bunu canlıda bildirdi: iş sürerken düğme yeniden etkinleşiyor, site
    /// oluşmamış gibi görünüyordu.
    /// </para>
    /// <para>
    /// Varsayılan <c>false</c>: kaza ile gelen ikinci istek artık 409 <c>sites.name_exists</c> alır.
    /// Alan adını BİLEREK yeniden ekleyen yönetici, paneldeki onay kutusundan sonra bu bayrakla
    /// gönderir — yani yıkıcı adım sessiz olmaktan çıkıp AÇIK bir karara dönüşür.
    /// </para>
    /// </remarks>
    public bool ReplaceExisting { get; init; }

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

    /// <summary>
    /// Bu istek PANELİN KENDİ altyapı sitesini kuruyor (webmail, phpMyAdmin, Adminer).
    /// <para>
    /// Kullanıcıdan gelen isteklerde <b>false kalmalıdır</b> ve API bağlamasına da girmez
    /// (JSON'dan okunmaz): aksi hâlde bir kullanıcı bayrağı göndererek korumayı devre dışı
    /// bırakabilirdi.
    /// </para>
    /// <para>
    /// Neden var: site oluşturma, aynı adlı IIS sitesini onay sormadan kaldırır (kaldırılmış bir
    /// kurulumdan kalan artık siteyi devralabilmek için — bu davranış bilinçlidir). Ama panelin
    /// kendi araçları da birer IIS sitesidir; kullanıcı o adlardan birini yazdığında çalışan
    /// phpMyAdmin/Adminer/webmail sessizce yok oluyordu.
    /// </para>
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore]
    public bool IsManagedInfrastructure { get; init; }
}
