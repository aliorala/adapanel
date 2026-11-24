using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed partial class FullMigrationCoordinator(
    MigrationJobStore jobs, IMigrationPackageStore packages,
    IEnumerable<IMigrationTargetAdapter> targetAdapters, IAuthService auth,
    ISecretProtector secrets) : IFullMigrationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, IMigrationTargetAdapter> adapters = targetAdapters.ToDictionary(adapter => adapter.Kind, StringComparer.Ordinal);
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> Running = new();

    public async Task RegisterPackagesAsync(Guid owner, IReadOnlyList<Guid> packageIds, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(owner, cancellationToken);
        if (packageIds.Count == 0 || packageIds.Count > 1000 || packageIds.Any(id => id == Guid.Empty)
            || packageIds.Distinct().Count() != packageIds.Count) throw Error("migration.package_selection_invalid");
        foreach (var id in packageIds) await packages.ReadManifestAsync(id, cancellationToken);
        await jobs.UpdateAsync(state =>
        {
            if (state.Packages.Count + packageIds.Count > 10000) throw Error("migration.history_limit");
            foreach (var id in packageIds)
            {
                var existing = state.Packages.SingleOrDefault(item => item.PackageId == id);
                if (existing is not null && existing.OwnerId != owner) throw Error("migration.package_not_found", 404);
                if (existing is null) state.Packages.Add(new() { OwnerId = owner, PackageId = id, CreatedAtUtc = DateTimeOffset.UtcNow });
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<PortableMigrationPackage>> GetPackagesAsync(Guid owner, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(owner, cancellationToken);
        var state = await jobs.ReadAsync(cancellationToken);
        var result = new List<PortableMigrationPackage>();
        foreach (var record in state.Packages.Where(record => record.OwnerId == owner))
            result.Add(new(record.PackageId, await packages.ReadManifestAsync(record.PackageId, cancellationToken)));
        return result;
    }

    public async Task<MigrationPlanDto> PlanAsync(Guid owner, MigrationPlanRequest request, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(owner, cancellationToken);
        ValidateSelection(request);
        // Own a snapshot: callers cannot change a plan by mutating the request after validation.
        request = JsonSerializer.Deserialize<MigrationPlanRequest>(JsonSerializer.Serialize(request, Json), Json)!;
        var state = await jobs.ReadAsync(cancellationToken);
        foreach (var id in request.PackageIds)
            if (!state.Packages.Any(item => item.OwnerId == owner && item.PackageId == id)) throw Error("migration.package_not_found", 404);
        var manifestMap = new Dictionary<Guid, PortableMigrationManifest>();
        foreach (var id in request.PackageIds) manifestMap.Add(id, await packages.ReadManifestAsync(id, cancellationToken));
        var plan = new MigrationPlanDto
        {
            Id = Guid.NewGuid(), OwnerId = owner, CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1), Selection = request,
            ManifestHashes = manifestMap.ToDictionary(pair => pair.Key, pair => Hash(pair.Value)),
            RequiredActions = ["migration.source_snapshot_not_atomic", "migration.final_sync_and_cutover_required"]
        };
        Assemble(plan, manifestMap);
        foreach (var item in plan.Items)
        {
            if (!adapters.TryGetValue(item.Kind, out var adapter)) { item.Blockers.Add("migration.target_adapter_unavailable"); continue; }
            try
            {
                var check = await adapter.CheckAsync(item, manifestMap[item.PackageId], request, cancellationToken);
                item.Blockers.AddRange(check.Blockers);
                item.Warnings.AddRange(check.Warnings);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { item.Blockers.Add(SafeError(error, "migration.target_check_failed")); }
        }
        // A duplicate target across source accounts must never race or silently merge.
        foreach (var group in plan.Items.GroupBy(item => item.Kind + ":" + item.Target.TargetName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            foreach (var item in group) item.Blockers.Add("migration.target_name_duplicate");
        plan.PlanHash = Hash(plan);
        await AuthorizeAsync(owner, cancellationToken);
        await jobs.UpdateAsync(current =>
        {
            if (current.Plans.Count >= 1000) throw Error("migration.history_limit");
            current.Plans.Add(new() { OwnerId = owner, Plan = plan });
        }, cancellationToken);
        return plan;
    }

    public async Task<MigrationOperationDto> StartAsync(Guid owner, Guid planId, string planHash, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(owner, cancellationToken);
        MigrationOperationDto? operation = null;
        await jobs.UpdateAsync(state =>
        {
            var stored = MigrationJobStore.RequirePlan(state, owner, planId);
            if (stored.Plan.PlanHash != planHash || !stored.Plan.CanApply) throw Error("migration.plan_not_applicable");
            // Duplicate clicks return the original operation, even if it failed. No SQL/mail replay.
            if (stored.OperationId is { } original)
            { operation = MigrationJobStore.RequireOperation(state, owner, original).Operation; return; }
            if (stored.Plan.ExpiresAtUtc <= DateTimeOffset.UtcNow) throw Error("migration.plan_expired");
            if (state.Operations.Any(item => item.Operation.State is "pending" or "running")) throw Error("migration.operation_already_running");
            if (state.Operations.Count >= 1000) throw Error("migration.history_limit");
            operation = new()
            {
                Id = Guid.NewGuid(), PlanId = planId, CreatedAtUtc = DateTimeOffset.UtcNow,
                RequiredActions = stored.Plan.RequiredActions.ToList(),
                Items = stored.Plan.Items.Select(item => new MigrationItemResultDto
                { Id = item.Id, Kind = item.Kind, SourceName = item.SourceName, TargetName = item.Target.TargetName }).ToList()
            };
            stored.OperationId = operation.Id;
            state.Operations.Add(new() { OwnerId = owner, Operation = operation });
        }, cancellationToken);
        return operation!;
    }

    public async Task<MigrationPlanDto> GetPlanAsync(Guid owner, Guid planId, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(owner, cancellationToken);
        return MigrationJobStore.RequirePlan(await jobs.ReadAsync(cancellationToken), owner, planId).Plan;
    }

    public async Task<IReadOnlyList<MigrationOperationDto>> GetOperationsAsync(Guid owner, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(owner, cancellationToken);
        return (await jobs.ReadAsync(cancellationToken)).Operations.Where(item => item.OwnerId == owner)
            .Select(item => item.Operation).OrderByDescending(item => item.CreatedAtUtc).ToList();
    }
    public async Task<MigrationOperationDto> GetOperationAsync(Guid owner, Guid operationId, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(owner, cancellationToken);
        return MigrationJobStore.RequireOperation(await jobs.ReadAsync(cancellationToken), owner, operationId).Operation;
    }
    public async Task<MigrationOperationDto> CancelAsync(Guid owner, Guid operationId, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(owner, cancellationToken);
        var state = await jobs.UpdateAsync(current =>
        {
            var operation = MigrationJobStore.RequireOperation(current, owner, operationId).Operation;
            if (operation.State is "pending" or "running") operation.CancellationRequested = true;
        }, cancellationToken);
        if (Running.TryGetValue(operationId, out var active))
        {
            try { active.Cancel(); }
            catch (ObjectDisposedException) { /* The worker completed after the dictionary lookup. */ }
        }
        return MigrationJobStore.RequireOperation(state, owner, operationId).Operation;
    }
    public async Task<IReadOnlyList<MigrationCreatedCredential>> GetCredentialsAsync(Guid owner, Guid operationId, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(owner, cancellationToken);
        var record = MigrationJobStore.RequireOperation(await jobs.ReadAsync(cancellationToken), owner, operationId);
        return ReadCredentials(record);
    }
    private List<MigrationCreatedCredential> ReadCredentials(OwnedMigrationOperation record) => string.IsNullOrWhiteSpace(record.ProtectedCredentials)
        ? [] : JsonSerializer.Deserialize<List<MigrationCreatedCredential>>(secrets.Unprotect(record.ProtectedCredentials), Json)
            ?? throw Error("migration.credentials_unreadable");

    private async Task AuthorizeAsync(Guid owner, CancellationToken ct)
    {
        if (owner == Guid.Empty) throw Error("migration.forbidden", 403);
        var user = await auth.GetUserByIdAsync(owner, ct);
        if (!user.IsActive || user.Role != "Administrator") throw Error("migration.forbidden", 403);
    }
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    private static AppException Error(string code, int status = 409) => new(code, status, status);
    private static string SafeError(Exception error, string fallback) => error is AppException app
        && Regex.IsMatch(app.MessageKey, @"\A[a-z][a-zA-Z0-9_.]{1,160}\z", RegexOptions.CultureInvariant) ? app.MessageKey : fallback;
}
