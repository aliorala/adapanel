using AdaPanel.Core.Dtos.Store;

namespace AdaPanel.Core.Interfaces;

public interface IAppStoreService
{
    Task<IReadOnlyCollection<AppTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default);

    Task<AppTemplateDto> InstallAsync(InstallAppTemplateRequest request, CancellationToken cancellationToken = default);
}
