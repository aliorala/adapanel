using AdaPanel.Core.Dtos.Asp;

namespace AdaPanel.Core.Interfaces;

public interface IAspNetService
{
    Task<AspSiteConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default);

    Task<AspSiteConfigurationDto> SetClassicAspAsync(string siteName, UpdateClassicAspRequest request, CancellationToken cancellationToken = default);

    Task<AspSiteConfigurationDto> SetDotNetVersionAsync(string siteName, UpdateDotNetRuntimeRequest request, CancellationToken cancellationToken = default);

    Task<AspSiteConfigurationDto> EnableMixedHandlersAsync(string siteName, EnableMixedHandlersRequest request, CancellationToken cancellationToken = default);
}
