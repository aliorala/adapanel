using AdaPanel.Core.Dtos.AppRuntimes;

namespace AdaPanel.Core.Interfaces;

public interface IAppRuntimeInventoryService
{
    Task<IReadOnlyCollection<AppRuntimeInventoryDto>> GetAsync(CancellationToken cancellationToken = default);
}
