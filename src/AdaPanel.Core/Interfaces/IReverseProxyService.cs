using AdaPanel.Core.Dtos.Proxy;

namespace AdaPanel.Core.Interfaces;

public interface IReverseProxyService
{
    Task<ReverseProxySiteConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default);

    Task<ReverseProxySiteConfigurationDto> EnableProxyAsync(
        string siteName,
        EnableReverseProxyRequest request,
        CancellationToken cancellationToken = default);

    Task<ReverseProxySiteConfigurationDto> RemoveProxyAsync(string siteName, CancellationToken cancellationToken = default);

    Task<ReverseProxySiteConfigurationDto> AddRewriteRuleAsync(
        string siteName,
        AddRewriteRuleRequest request,
        CancellationToken cancellationToken = default);
}
