using AdaPanel.Core.Dtos.Databases;

namespace AdaPanel.Core.Interfaces;

/// <summary>Fresh-only restore boundary. Never restores using administrative credentials.</summary>
public interface IMigrationDatabaseImporter
{
    Task<MigrationDatabasePlan> PlanAsync(MigrationDatabaseImportRequest request, CancellationToken cancellationToken = default);
    Task<MigrationDatabaseImportResult> ImportAsync(MigrationDatabaseImportRequest request, CancellationToken cancellationToken = default);
}
