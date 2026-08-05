using AdaPanel.Core.Dtos.Waf;

namespace AdaPanel.Core.Interfaces;

public interface IWafService
{
    Task<SiteSecurityConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteSecurityConfigurationDto> IsolateAppPoolAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteSecurityConfigurationDto> ApplyFolderAclAsync(string siteName, ApplyFolderAclRequest request, CancellationToken cancellationToken = default);

    Task<SiteSecurityConfigurationDto> SetRateLimitAsync(string siteName, SetSiteRateLimitRequest request, CancellationToken cancellationToken = default);

    Task<SiteSecurityConfigurationDto> EnableBasicRulesAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Site silindiğinde onun güvenlik durum kaydını (izolasyon, klasör izni, hız sınırı) kaldırır.
    /// <para>
    /// Kayıt kalırsa AYNI ADLA açılan yeni bir site, hiç uygulanmamış korumaları uygulanmış
    /// gösterir — panel yine kendi kaydını gerçek sanmış olur.
    /// </para>
    /// </summary>
    Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default);
}
