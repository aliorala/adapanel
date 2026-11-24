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

    /// <summary>Uygulama havuzunu durdurur (hosting edilen Node/Python süreci havuzla birlikte ölür).</summary>
    Task<AppRuntimeSiteConfigurationDto> StopAppAsync(string siteName,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default);

    Task<AppRuntimeSiteConfigurationDto> StartAppAsync(string siteName,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default);

    Task<AppRuntimeSiteConfigurationDto> RestartAppAsync(string siteName,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Barındırmayı kaldırır: süreci durdurur, web.config'teki panel girdilerini siler (kullanıcının
    /// diğer ayarlarına dokunmaz), istenirse panelin günlük klasörünü siler. Uygulama KAYNAK
    /// dosyalarını silmez. Kalan her şey sonuçta <c>Leftovers</c> olarak bildirilir.
    /// </summary>
    Task<AppRuntimeRemovalResultDto> RemoveAppAsync(string siteName, bool deleteLogs,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default);

    Task<AppRuntimeLogsDto> GetLogsAsync(string siteName, int tail, CancellationToken cancellationToken = default);
}
