using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Core.Interfaces;

public interface IAdaPanelMigrationExporter
{
    Task<MigrationSourceInventoryDto> GetInventoryAsync(CancellationToken cancellationToken = default);
    Task<MigrationExportArtifact> ExportAsync(MigrationExportRequest request, CancellationToken cancellationToken = default);
}
