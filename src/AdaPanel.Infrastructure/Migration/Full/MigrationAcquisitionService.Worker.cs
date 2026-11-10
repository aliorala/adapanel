using System.Text.Json;
using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed partial class MigrationAcquisitionService
{
    public async Task<bool> RunNextAsync(CancellationToken stoppingToken = default)
    {
        if (!await WorkerGate.WaitAsync(0, stoppingToken)) return false;
        try
        {
            var next = (await jobs.ReadAsync(stoppingToken)).Acquisitions.FirstOrDefault(r => r.Acquisition.State == "pending");
            if (next is null) return false;
            var owner = next.OwnerId; var id = next.Acquisition.Id;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Running[id] = cancellation;
            var errorCode = ""; var unknownCompletion = false; var budgetExceeded = false; var completed = false;
            try
            {
                await CheckpointAsync(owner, id, cancellation);
                var state = await jobs.UpdateAsync(current =>
                {
                    var record = MigrationJobStore.RequireAcquisition(current, owner, id);
                    if (record.Acquisition.State != "pending") throw Error("migration.acquisition_state_changed");
                    record.Acquisition.State = "running"; record.Acquisition.Stage = "authorizing";
                    record.Acquisition.StartedAtUtc = DateTimeOffset.UtcNow;
                }, cancellation.Token);
                var record = MigrationJobStore.RequireAcquisition(state, owner, id);
                ProtectedAcquisition bound;
                try { bound = JsonSerializer.Deserialize<ProtectedAcquisition>(secrets.Unprotect(record.ProtectedRequest), Json) ?? throw new InvalidDataException(); }
                catch { throw Error("migration.acquisition_credentials_unavailable"); }
                var request = bound.Request;
                if (bound.OwnerId != owner || bound.JobId != id || bound.ClientRequestId != record.Acquisition.ClientRequestId
                    || request.ClientRequestId != bound.ClientRequestId || ValidateConnection(request.Connection) != record.Acquisition.SourceHost
                    || request.Connection.Provider != record.Acquisition.Provider || !PublicSelection(request).SequenceEqual(record.Acquisition.SourceUnitIds))
                    throw Error("migration.acquisition_request_changed");
                ValidateSelection(request);
                await EnsurePrivateStorageAsync(cancellation.Token);
                if (request.Connection.Provider == "adapanel")
                {
                    await SetStage(owner, id, "discovering", cancellation.Token);
                    await CheckpointAsync(owner, id, cancellation);
                    var inventory = await nativeSource.DiscoverAsync(request.Connection.AdaPanel!, cancellation.Token);
                    var expected = NativeScope(inventory, request.AdaPanelSelection);
                    await CheckpointAsync(owner, id, cancellation);
                    await SetStage(owner, id, "acquiring", cancellation.Token);
                    var package = await nativeSource.AcquireAsync(request.Connection.AdaPanel!, request.AdaPanelSelection, cancellation.Token);
                    await JournalCandidateAsync(owner, id, package, "adapanel", "");
                    await CheckpointAsync(owner, id, cancellation);
                    await AcceptPackageAsync(owner, id, package, "adapanel", "", expected, request.AdaPanelSelection, cancellation);
                    await jobs.UpdateAsync(current => AddWarning(MigrationJobStore.RequireAcquisition(current, owner, id).Acquisition,
                        "migration.acquisition_remote_cleanup_unmeasured"), CancellationToken.None);
                }
                else
                {
                    await SetStage(owner, id, "discovering", cancellation.Token);
                    var inventory = await sshSource.DiscoverAsync(request.Connection.Ssh!, cancellation.Token);
                    if (!inventory.Supported || inventory.ErrorCode.Length != 0 || inventory.Units.Any(u => u.Provider != request.Connection.Provider || !ValidUnitId(request.Connection.Provider, u.Id))
                        || request.SourceUnitIds.Any(unit => inventory.Units.Count(u => u.Id == unit) != 1)) throw Error("migration.acquisition_scope_changed");
                    foreach (var unit in request.SourceUnitIds)
                    {
                        await CheckpointAsync(owner, id, cancellation);
                        state = await jobs.ReadAsync(cancellation.Token);
                        var status = MigrationJobStore.RequireAcquisition(state, owner, id).Acquisition;
                        if (status.DownloadedBytes >= liveLimits.Value.MaxTotalArtifactBytes || status.NormalizedPayloadBytes >= TotalPayloadLimit)
                            throw Error("migration.acquisition_budget_exceeded");
                        var workspace = await CreateUnitWorkspaceAsync(owner, id, unit, cancellation.Token);
                        var priorBytes = status.DownloadedBytes ?? 0;
                        await SetStage(owner, id, "acquiring", cancellation.Token);
                        long reported = 0;
                        var progress = new InlineProgress(update =>
                        {
                            if (update.Bytes <= reported) return;
                            reported = update.Bytes;
                            if (reported > liveLimits.Value.MaxTotalArtifactBytes - priorBytes)
                            { budgetExceeded = true; cancellation.Cancel(); }
                        });
                        var acquired = await sshSource.AcquireAsync(request.Connection.Ssh!, [unit], workspace, progress, cancellation.Token);
                        await SaveRecoveryAsync(owner, id, unit, workspace, acquired, CancellationToken.None);
                        if (!acquired.ProcessCompletionConfirmed) { unknownCompletion = true; throw Error("migration.acquisition_completion_unknown"); }
                        if (budgetExceeded) throw Error("migration.acquisition_budget_exceeded");
                        await CheckpointAsync(owner, id, cancellation);
                        if (acquired.Cancelled) throw new OperationCanceledException(cancellation.Token);
                        if (acquired.Artifacts.Count > 1) throw Error("migration.acquisition_source_invalid");
                        if (acquired.Artifacts.Count == 0) throw Error("migration.acquisition_source_failed");
                        foreach (var artifact in acquired.Artifacts)
                        {
                            await CheckpointAsync(owner, id, cancellation);
                            await SetStage(owner, id, "validating-source", cancellation.Token);
                            await using var stream = await OpenVerifiedSourceAsync(owner, id, workspace, unit, request.Connection.Provider, artifact, cancellation.Token);
                            await SetStage(owner, id, "normalizing", cancellation.Token);
                            await CheckpointAsync(owner, id, cancellation);
                            var package = request.Connection.Provider == "cpanel"
                                ? await (CpanelNormalizer is null ? cpanel.NormalizeAsync(stream, cancellation.Token) : CpanelNormalizer(stream, cancellation.Token))
                                : await (PleskNormalizer is null ? plesk.NormalizeFullExportAsync(stream, artifact.Format, unit[6..], cancellation.Token) : PleskNormalizer(stream, artifact.Format, unit[6..], cancellation.Token));
                            await JournalCandidateAsync(owner, id, package, request.Connection.Provider, unit);
                            await CheckpointAsync(owner, id, cancellation);
                            await AcceptPackageAsync(owner, id, package, request.Connection.Provider, unit, null, null, cancellation);
                        }
                        if (acquired.ErrorCode.Length != 0) throw Error("migration.acquisition_source_failed");
                        if (acquired.RemoteCleanupComplete) await CleanupUnitAsync(owner, id, workspace, CancellationToken.None);
                    }
                }
                await CheckpointAsync(owner, id, cancellation);
                completed = true;
            }
            catch (Exception error)
            {
                errorCode = budgetExceeded ? "migration.acquisition_budget_exceeded"
                    : unknownCompletion ? "migration.acquisition_completion_unknown"
                    : error is OperationCanceledException ? (stoppingToken.IsCancellationRequested ? "migration.acquisition_interrupted" : "migration.acquisition_cancelled")
                    : error is AdaPanel.Core.Common.AppException app && AllowedErrors.Contains(app.MessageKey) ? app.MessageKey : "migration.acquisition_failed";
            }
            finally
            {
                try
                {
                await RecoverLocalJournalsAsync(owner, id, CancellationToken.None);
                await jobs.UpdateAsync(current =>
                {
                    var record = MigrationJobStore.RequireAcquisition(current, owner, id);
                    ReconcileRegistrations(current, record);
                    if (completed && !record.Acquisition.CancellationRequested && record.Acquisition.PackageIds.Count > 0)
                    { record.Acquisition.State = "completed"; record.Acquisition.Stage = "completed"; }
                    else
                    {
                        if (errorCode.Length == 0) errorCode = record.Acquisition.CancellationRequested ? "migration.acquisition_cancelled" : "migration.acquisition_failed";
                        record.Acquisition.State = record.Acquisition.PackageIds.Count > 0 ? "partial"
                            : unknownCompletion || stoppingToken.IsCancellationRequested ? "interrupted"
                            : record.Acquisition.CancellationRequested || cancellation.IsCancellationRequested && !budgetExceeded ? "cancelled" : "failed";
                        record.Acquisition.Stage = record.Acquisition.State; record.Acquisition.ErrorCode = errorCode;
                    }
                    PublishCandidateRecoveries(record);
                    record.ProtectedRequest = ""; record.Acquisition.FinishedAtUtc = DateTimeOffset.UtcNow;
                    if (record.LocalWorkspaces.Count > 0) AddWarning(record.Acquisition, "migration.acquisition_local_recovery_retained");
                }, CancellationToken.None);
                }
                finally { Running.TryRemove(id, out _); }
            }
            return true;
        }
        finally { WorkerGate.Release(); }
    }

    public async Task RecoverInterruptedAsync(CancellationToken ct = default)
    {
        await WorkerGate.WaitAsync(ct);
        try
        {
            var running = (await jobs.ReadAsync(ct)).Acquisitions.Where(r => r.Acquisition.State == "running").ToArray();
            foreach (var record in running) await RecoverLocalJournalsAsync(record.OwnerId, record.Acquisition.Id, ct);
            await jobs.UpdateAsync(state =>
            {
                foreach (var record in state.Acquisitions)
                {
                    ReconcileRegistrations(state, record);
                    if (record.Acquisition.State == "running")
                    {
                        record.Acquisition.State = "interrupted"; record.Acquisition.Stage = "interrupted";
                        record.Acquisition.ErrorCode = "migration.acquisition_interrupted";
                        record.Acquisition.FinishedAtUtc = DateTimeOffset.UtcNow;
                        AddWarning(record.Acquisition, "migration.acquisition_interrupted_review_required");
                        if (record.LocalWorkspaces.Count > 0) AddWarning(record.Acquisition, "migration.acquisition_local_recovery_retained");
                    }
                    if (record.Acquisition.State is not ("pending" or "running")) { record.ProtectedRequest = ""; PublishCandidateRecoveries(record); }
                }
            }, ct);
        }
        finally { WorkerGate.Release(); }
    }
    private async Task CheckpointAsync(Guid owner, Guid id, CancellationTokenSource cancellation)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        await AuthorizeAsync(owner, cancellation.Token);
        var record = MigrationJobStore.RequireAcquisition(await jobs.ReadAsync(cancellation.Token), owner, id);
        if (record.Acquisition.CancellationRequested) cancellation.Cancel();
        cancellation.Token.ThrowIfCancellationRequested();
    }
    private Task SetStage(Guid owner, Guid id, string stage, CancellationToken ct) => jobs.UpdateAsync(state =>
        MigrationJobStore.RequireAcquisition(state, owner, id).Acquisition.Stage = stage, ct);
    private static void ReconcileRegistrations(MigrationJobState state, OwnedMigrationAcquisition record)
    {
        foreach (var id in record.PendingPackageIds.ToArray())
            if (state.Packages.Any(package => package.PackageId == id && package.OwnerId == record.OwnerId))
            { if (!record.Acquisition.PackageIds.Contains(id)) record.Acquisition.PackageIds.Add(id); record.PendingPackageIds.Remove(id); record.CandidatePackages.RemoveAll(candidate => candidate.PackageId == id); }
    }
    private sealed class InlineProgress(Action<LiveMigrationProgress> report) : IProgress<LiveMigrationProgress>
    { public void Report(LiveMigrationProgress value) => report(value); }
    private static readonly HashSet<string> AllowedErrors = ["migration.forbidden", "migration.acquisition_credentials_unavailable", "migration.acquisition_request_changed",
        "migration.acquisition_scope_changed", "migration.acquisition_source_invalid", "migration.acquisition_source_failed", "migration.acquisition_budget_exceeded",
        "migration.acquisition_package_changed", "migration.acquisition_package_invalid", "migration.acquisition_storage_unverified"];
}
