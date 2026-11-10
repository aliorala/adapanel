using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Migration.Full;

// Stored under the private migration root. Secrets are additionally protected by ISecretProtector.
// One atomic state document keeps ownership, plan consumption and pending jobs consistent.
public sealed class MigrationJobStore(JsonFileStore files, IIisSiteService? sites = null, IOptions<PanelRuntimeOptions>? runtime = null)
{
    private const string StatePath = "migration-private/jobs.json";
    public async Task<MigrationJobState> ReadAsync(CancellationToken ct = default)
    { await EnsurePrivateRootAsync(ct); return await files.ReadAsync(StatePath, new MigrationJobState(), ct); }
    public async Task<MigrationJobState> UpdateAsync(Action<MigrationJobState> mutation, CancellationToken ct = default)
    {
        await EnsurePrivateRootAsync(ct);
        return await files.UpdateAsync(StatePath, new MigrationJobState(), state => { mutation(state); return state; }, ct);
    }
    private async Task EnsurePrivateRootAsync(CancellationToken ct)
    {
        if (sites is null && runtime is null) return; // Existing isolated tests own their temporary root.
        if (sites is null || runtime is null) throw new AppException("migration.private_storage_unverified", 409, 409);
        var inventory = await sites.GetMigrationTargetSitesAsync(ct);
        if (inventory is null || inventory.Any(site => string.IsNullOrWhiteSpace(site.PhysicalPath) && string.IsNullOrWhiteSpace(site.ApplicationRootPath)))
            throw new AppException("migration.private_storage_unverified", 409, 409);
        var publicRoots = inventory.SelectMany(site => new[] { site.PhysicalPath, site.ApplicationRootPath }).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))).ToArray();
        var root = Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(runtime.Value.DataRoot), "migration-private"));
        if (publicRoots.Any(web => web.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new AppException("migration.private_storage_unverified", 409, 409);
        PortableMigrationStorage.CreatePrivateDirectory(root, publicRoots);
    }

    internal static OwnedMigrationAcquisition RequireAcquisition(MigrationJobState state, Guid owner, Guid id)
        => state.Acquisitions.SingleOrDefault(item => item.OwnerId == owner && item.Acquisition.Id == id)
            ?? throw new AppException("migration.acquisition_not_found", 404, 404);
    internal static OwnedMigrationOperation RequireOperation(MigrationJobState state, Guid owner, Guid id)
        => state.Operations.SingleOrDefault(item => item.OwnerId == owner && item.Operation.Id == id)
            ?? throw new AppException("migration.operation_not_found", 404, 404);
    internal static OwnedMigrationPlan RequirePlan(MigrationJobState state, Guid owner, Guid id)
        => state.Plans.SingleOrDefault(item => item.OwnerId == owner && item.Plan.Id == id)
            ?? throw new AppException("migration.plan_not_found", 404, 404);
}

public sealed class MigrationJobState
{
    public int Version { get; set; } = 1;
    public List<OwnedMigrationAcquisition> Acquisitions { get; set; } = [];
    public List<OwnedMigrationPackage> Packages { get; set; } = [];
    public List<OwnedMigrationPlan> Plans { get; set; } = [];
    public List<OwnedMigrationOperation> Operations { get; set; } = [];
}
public sealed class OwnedMigrationPackage
{
    public Guid OwnerId { get; set; }
    public Guid PackageId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
public sealed class OwnedMigrationPlan
{
    public Guid OwnerId { get; set; }
    public MigrationPlanDto Plan { get; set; } = new();
    public Guid? OperationId { get; set; }
}
public sealed class OwnedMigrationOperation
{
    public Guid OwnerId { get; set; }
    public MigrationOperationDto Operation { get; set; } = new();
    public string ProtectedCredentials { get; set; } = "";
}

public sealed class OwnedMigrationAcquisition
{
    public Guid OwnerId { get; set; }
    public MigrationAcquisitionDto Acquisition { get; set; } = new();
    public string ProtectedRequest { get; set; } = "";
    public List<string> LocalWorkspaces { get; set; } = [];
    public Dictionary<string, string> LocalWorkspaceUnits { get; set; } = [];
    public List<MigrationAcquisitionPackageRecoveryDto> CandidatePackages { get; set; } = [];
    public List<Guid> PendingPackageIds { get; set; } = [];
    public Dictionary<Guid, string> VerifiedPackageHashes { get; set; } = [];
}
