using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Core.Interfaces;

public interface IFullMigrationService
{
    Task RegisterPackagesAsync(Guid owner, IReadOnlyList<Guid> packageIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PortableMigrationPackage>> GetPackagesAsync(Guid owner, CancellationToken cancellationToken = default);
    Task<MigrationPlanDto> PlanAsync(Guid owner, MigrationPlanRequest request, CancellationToken cancellationToken = default);
    Task<MigrationPlanDto> GetPlanAsync(Guid owner, Guid planId, CancellationToken cancellationToken = default);
    Task<MigrationOperationDto> StartAsync(Guid owner, Guid planId, string planHash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MigrationOperationDto>> GetOperationsAsync(Guid owner, CancellationToken cancellationToken = default);
    Task<MigrationOperationDto> GetOperationAsync(Guid owner, Guid operationId, CancellationToken cancellationToken = default);
    Task<MigrationOperationDto> CancelAsync(Guid owner, Guid operationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MigrationCreatedCredential>> GetCredentialsAsync(Guid owner, Guid operationId, CancellationToken cancellationToken = default);
}

public interface IMigrationTargetAdapter
{
    string Kind { get; }
    Task<MigrationTargetCheck> CheckAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest,
        MigrationPlanRequest selection, CancellationToken cancellationToken);
    IReadOnlyList<MigrationCreatedCredential> PrepareCredentials(MigrationPlanItemDto item, PortableMigrationManifest manifest);
    Task<MigrationComponentResult> ApplyAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest,
        MigrationPlanRequest selection, IReadOnlyList<MigrationCreatedCredential> credentials, CancellationToken cancellationToken);
}
