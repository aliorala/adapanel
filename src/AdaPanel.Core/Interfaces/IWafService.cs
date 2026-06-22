using AdaPanel.Core.Dtos.Waf;

namespace AdaPanel.Core.Interfaces;

public interface IWafService
{
    Task<SiteSecurityConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteSecurityConfigurationDto> IsolateAppPoolAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteSecurityConfigurationDto> ApplyFolderAclAsync(string siteName, ApplyFolderAclRequest request, CancellationToken cancellationToken = default);

    Task<SiteSecurityConfigurationDto> SetRateLimitAsync(string siteName, SetSiteRateLimitRequest request, CancellationToken cancellationToken = default);

    Task<SiteSecurityConfigurationDto> EnableBasicRulesAsync(string siteName, CancellationToken cancellationToken = default);
}
