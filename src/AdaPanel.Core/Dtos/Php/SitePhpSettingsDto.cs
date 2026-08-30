using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Php;

/// <summary>
/// Site başına PHP ayarları (panel "PHP Settings"). Sitenin kök dizinindeki <c>.user.ini</c>
/// dosyasına yazılır; PHP bu dosyayı per-dizin okur (FastCGI/CGI).
/// </summary>
public sealed class SitePhpSettingsDto
{
    public string SiteName { get; set; } = string.Empty;

    public string MemoryLimit { get; set; } = "256M";

    public int MaxExecutionTime { get; set; } = 120;

    public string UploadMaxFilesize { get; set; } = "64M";

    public string PostMaxSize { get; set; } = "64M";

    public bool DisplayErrors { get; set; }

    /// <summary>
    /// open_basedir izolasyonu açık mı. Açıkken PHP sitenin köküne (Laravel'de ApplicationRootPath,
    /// yani public'in ÜSTÜ) sınırlanır; yükleme/oturum/temp de site içindeki <c>tmp</c>'ye bağlanır,
    /// böylece ortak OS temp izin listesinden tamamen düşer. Diğer siteleri / panel verisini okuyamaz.
    /// <para>
    /// YENİ SİTELERDE VARSAYILAN AÇIK (paylaşımlı barındırmanın tanımı budur). Değer sitenin kökündeki
    /// <c>.user.ini</c>'den DEĞİL, paylaşılan php.ini'deki <c>[PATH=]</c> bloğundan okunur — kiracının
    /// silebildiği bir dosya izolasyon durumunun kaynağı olamaz.
    /// </para>
    /// </summary>
    public bool OpenBasedirEnabled { get; set; }

    /// <summary>.user.ini mevcut mu (yani daha önce özelleştirildi mi).</summary>
    public bool Configured { get; set; }

    /// <summary>phpredis (Redis) uzantısı PHP'de yüklü/etkin mi (global). UI'de "Etkin/Devre dışı" rozeti için.</summary>
    public bool RedisEnabled { get; set; }
}

public sealed class UpdateSitePhpSettingsRequest
{
    [Required]
    [RegularExpression(@"^-?\d+[KMG]?$")]
    [StringLength(16)]
    public string MemoryLimit { get; init; } = "256M";

    [Range(0, 3600)]
    public int MaxExecutionTime { get; init; } = 120;

    [Required]
    [RegularExpression(@"^\d+[KMG]?$")]
    [StringLength(16)]
    public string UploadMaxFilesize { get; init; } = "64M";

    [Required]
    [RegularExpression(@"^\d+[KMG]?$")]
    [StringLength(16)]
    public string PostMaxSize { get; init; } = "64M";

    public bool DisplayErrors { get; init; }

    /// <summary>
    /// open_basedir izolasyonunu aç/kapat. <c>null</c> = DOKUNMA (mevcut durum korunur).
    /// </summary>
    /// <remarks>
    /// Neden <c>bool?</c>: alan eskiden <c>bool</c> idi ve JSON'da hiç gönderilmediğinde <c>false</c>
    /// oluyordu. Yani yalnız "memory_limit" güncelleyen eski bir arayüz/istemci, farkında olmadan
    /// sitenin KİRACI İZOLASYONUNU KAPATIRDI. Bu depoda "bir taraf yazıyor, öteki sessizce
    /// başka şey anlıyor" hata sınıfı defalarca ölçüldü; söylenmeyen alan artık DEĞİŞTİRMEZ.
    /// </remarks>
    public bool? OpenBasedirEnabled { get; init; }
}
