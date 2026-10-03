using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface IAppRuntimeService
{
    Task<AppRuntimeSiteConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default);

    Task<AppRuntimeSiteConfigurationDto> HostNodeAppAsync(
        string siteName,
        HostNodeAppRequest request,
        CancellationToken cancellationToken = default);

    Task<AppRuntimeSiteConfigurationDto> HostNodeAppAsync(string siteName, HostNodeAppRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default);

    Task<AppRuntimeSiteConfigurationDto> HostPythonAppAsync(
        string siteName,
        HostPythonAppRequest request,
        CancellationToken cancellationToken = default);

    Task<AppRuntimeSiteConfigurationDto> HostPythonAppAsync(string siteName, HostPythonAppRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default);
}
