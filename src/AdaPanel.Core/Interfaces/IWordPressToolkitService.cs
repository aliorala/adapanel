using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// panel WordPress Toolkit benzeri temel yönetim: kurulum algılama, sürüm okuma ve
/// bakım modu (WP-CLI gerektirmeden, dosya tabanlı).
/// </summary>
public interface IWordPressToolkitService
{
    /// <summary>Verilen sitedeki WordPress kurulumunu algılar (yoksa IsWordPress=false).</summary>
    Task<WordPressSiteDto> DetectAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Tüm yönetilen sitelerdeki WordPress kurulumlarını listeler.</summary>
    Task<IReadOnlyCollection<WordPressSiteDto>> DetectAllAsync(CancellationToken cancellationToken = default);

    Task<WordPressSiteDto> SetMaintenanceAsync(string siteName, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// panel'deki "Giriş yap" gibi: yöneticiyi siteye tek tıkla sokan, tek kullanımlık ve kısa ömürlü
    /// bir sihirli bağlantı üretir (geçici, kendini silen mu-plugin ile).
    /// </summary>
    Task<WordPressLoginLinkDto> CreateLoginLinkAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// panel "Güvenlik" gibi: bir önlem demetini uygular — sürüm gizleme, XML-RPC pingback kapatma,
    /// yazar taraması engelleme (mu-plugin) + dosya düzenleyici kapatma + güvenlik anahtarlarını yenileme (wp-cli).
    /// </summary>
    Task<WordPressHardenResultDto> HardenAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// panel "Klonla" gibi: kaynak WordPress sitesini yeni bir hedefe (staging) kopyalar — site oluşturma +
    /// dosya kopyası + ayrı bir veritabanı kopyası + wp-config DB bilgilerinin yenilenmesi + URL arama-değiştir.
    /// </summary>
    Task<WordPressCloneResultDto> CloneAsync(string sourceSiteName, CloneWordPressRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// panel "Veri Kopyala" gibi: kaynak WP'nin dosya ve/veya veritabanını VAR OLAN bir hedefe taşır
    /// (ör. staging→canlı). Hedefin DB bağlantısı korunur, URL arama-değiştir yapılır. ÖNCE hedefin yedeği alınır.
    /// </summary>
    Task<WordPressCopyDataResultDto> CopyDataAsync(string sourceSiteName, CopyWordPressDataRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sitedeki güncellenebilir öğeleri (çekirdek + eklenti + tema) listeler.
    /// </summary>
    /// <remarks>
    /// Panel bugüne kadar WordPress'i yalnız OKUYORDU; güncelleme yönetimi yoktu. Oysa güncel
    /// olmayan eklenti, barındırmada sitelerin ele geçirilme sebeplerinin başında gelir.
    /// </remarks>
    Task<WordPressUpdatesDto> GetUpdatesAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Seçilen öğeleri günceller; her biri için ayrı sonuç döner.</summary>
    Task<WordPressUpdateResultDto> ApplyUpdatesAsync(string siteName, WordPressUpdateRequest request, CancellationToken cancellationToken = default);

}
