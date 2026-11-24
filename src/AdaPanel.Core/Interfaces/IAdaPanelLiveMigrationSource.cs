using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Core.Interfaces;

public interface IAdaPanelLiveMigrationSource
{
    Task<MigrationSourceInventoryDto> DiscoverAsync(AdaPanelSourceConnection connection, CancellationToken cancellationToken = default);
    Task<PortableMigrationPackage> AcquireAsync(AdaPanelSourceConnection connection, MigrationExportRequest selection, CancellationToken cancellationToken = default);
}
