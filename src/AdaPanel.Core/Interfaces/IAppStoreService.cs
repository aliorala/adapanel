using AdaPanel.Core.Dtos.Store;

namespace AdaPanel.Core.Interfaces;

public interface IAppStoreService
{
    Task<IReadOnlyCollection<AppTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default);

    Task<AppInstallResultDto> InstallAsync(InstallAppTemplateRequest request, CancellationToken cancellationToken = default);
}
