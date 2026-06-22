using AdaPanel.Core.Dtos.AppRuntimes;

namespace AdaPanel.Core.Interfaces;

public interface IAppRuntimeService
{
    Task<AppRuntimeSiteConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default);

    Task<AppRuntimeSiteConfigurationDto> HostNodeAppAsync(
        string siteName,
        HostNodeAppRequest request,
        CancellationToken cancellationToken = default);

    Task<AppRuntimeSiteConfigurationDto> HostPythonAppAsync(
        string siteName,
        HostPythonAppRequest request,
        CancellationToken cancellationToken = default);
}
