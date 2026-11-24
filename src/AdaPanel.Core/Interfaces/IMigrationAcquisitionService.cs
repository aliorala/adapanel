using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Core.Interfaces;

public interface IMigrationAcquisitionService
{
    Task<MigrationDiscoveryDto> DiscoverAsync(Guid owner, MigrationConnectionRequest connection, CancellationToken ct = default);
    Task<MigrationAcquisitionDto> StartAsync(Guid owner, MigrationAcquireRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MigrationAcquisitionDto>> ListAsync(Guid owner, CancellationToken ct = default);
    Task<MigrationAcquisitionDto> GetAsync(Guid owner, Guid acquisitionId, CancellationToken ct = default);
    Task<MigrationAcquisitionDto> CancelAsync(Guid owner, Guid acquisitionId, CancellationToken ct = default);
}
