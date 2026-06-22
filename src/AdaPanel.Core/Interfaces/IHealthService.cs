using AdaPanel.Core.Dtos.Health;

namespace AdaPanel.Core.Interfaces;

public interface IHealthService
{
    Task<IReadOnlyCollection<ServiceHealthDto>> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<ServiceHealthDto> RestartServiceAsync(string name, CancellationToken cancellationToken = default);
}
