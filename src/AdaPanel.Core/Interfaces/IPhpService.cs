using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Php;

namespace AdaPanel.Core.Interfaces;

public interface IPhpService
{
    Task<IReadOnlyCollection<PhpVersionDto>> GetInstalledVersionsAsync(CancellationToken cancellationToken = default);

    Task<ComponentInstallResultDto> InstallVersionAsync(
        string componentId,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default,
        bool force = false);

    Task RegisterFastCgiAsync(string componentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verilen eklentileri PHP'nin php.ini'sinde etkinleştirir (DLL'i ext\ altında varsa).
    /// Zaten kurulu PHP'de de çalışır; Roundcube gibi ek eklenti isteyen uygulamalar için.
    /// </summary>
    Task EnsureExtensionsAsync(string componentId, IEnumerable<string> extensions, CancellationToken cancellationToken = default);

    Task<SitePhpAssignmentDto> SetSitePhpVersionAsync(
        string siteName,
        SetSitePhpVersionRequest request,
        CancellationToken cancellationToken = default);

    Task<SitePhpSettingsDto> GetSitePhpSettingsAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Site başına PHP kiracı izolasyonunu (open_basedir + site-içi yükleme/oturum/temp) paylaşılan
    /// php.ini'nin <c>[PATH=...]</c> bölümüne yazar ya da kaldırır. YENİ SİTELERDE VARSAYILAN AÇIK.
    /// </summary>
    /// <remarks>
    /// Neden site kökündeki <c>.user.ini</c> değil: o dosyayı kiracının kendi kimliği silebilir
    /// (site kökü ona Modify açıktır) — yani savunmayı savunulan taraf kaldırabilirdi. php.ini
    /// kiracının erişemediği yerdedir ve PHP, <c>[PATH=]</c> yönergelerinin kullanıcı ini'leriyle
    /// ezilmesine izin vermez (bu depoda php-cgi 8.3 ile ölçüldü).
    /// </remarks>
    Task EnsureSiteIsolationAsync(string siteName, bool enabled, CancellationToken cancellationToken = default);

    Task<SitePhpSettingsDto> UpdateSitePhpSettingsAsync(
        string siteName,
        UpdateSitePhpSettingsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Kurulu PHP'nin GENEL php.ini ayarlarını (memory_limit, max_execution_time, upload_max_filesize,
    /// post_max_size) okur. phpMyAdmin gibi ARAÇLAR bu global php.ini'yi kullanır → "İçe Aktar" limiti buradan gelir.
    /// </summary>
    Task<SitePhpSettingsDto> GetGlobalPhpSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Global php.ini ayarlarını yazar (+ FastCGI timeout'u yükseltir + php-cgi'yi yeniden başlatır).
    /// phpMyAdmin/Adminer ve tüm PHP'nin varsayılan limitlerini panelden değiştirmeyi sağlar.
    /// </summary>
    Task<SitePhpSettingsDto> UpdateGlobalPhpSettingsAsync(
        UpdateSitePhpSettingsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Native phpredis (redis) PHP uzantısını kurulu PHP'ye indirip etkinleştirir. PHP build'ine (sürüm/TS/mimari)
    /// uyan DLL indirilir, php.ini'ye eklenir, "php -m" ile DOĞRULANIR; yüklenmezse GERİ ALINIR (PHP kırılmaz).
    /// </summary>
    Task<RedisExtensionResultDto> EnableRedisExtensionAsync(CancellationToken cancellationToken = default);
}
