using System.Text.Json;
using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed partial class FullMigrationCoordinator
{
    // Called once when the hosted worker starts. An interrupted native mutation is never replayed.
    public Task RecoverInterruptedAsync(CancellationToken ct = default) => jobs.UpdateAsync(state =>
    {
        foreach (var record in state.Operations.Where(record => record.Operation.State == "running"))
        {
            record.Operation.State = "interrupted";
            record.Operation.FinishedAtUtc = DateTimeOffset.UtcNow;
            record.Operation.RequiredActions.Add("migration.interrupted_review_required");
            foreach (var item in record.Operation.Items)
                if (item.State == "running") { item.State = "interrupted"; item.PartialStatePossible = true; }
                else if (item.State == "pending") item.State = "not_started";
        }
    }, ct);

    // A hosted worker owns execution; HTTP cancellation only stops polling, not the migration.
    public async Task<bool> RunNextAsync(CancellationToken stoppingToken = default)
    {
        if (!await WorkerGate.WaitAsync(0, stoppingToken)) return false;
        try
        {
            var state = await jobs.ReadAsync(stoppingToken);
            var next = state.Operations.FirstOrDefault(record => record.Operation.State == "pending");
            if (next is null) return false;
            var owner = next.OwnerId;
            var id = next.Operation.Id;
            var plan = MigrationJobStore.RequirePlan(state, owner, next.Operation.PlanId).Plan;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Running[id] = cancellation;
            try
            {
                await AuthorizeAsync(owner, cancellation.Token);
                foreach (var (packageId, expected) in plan.ManifestHashes)
                    if (Hash(await packages.ReadManifestAsync(packageId, cancellation.Token)) != expected) throw Error("migration.package_changed");
                await jobs.UpdateAsync(current =>
                {
                    var operation = MigrationJobStore.RequireOperation(current, owner, id).Operation;
                    if (operation.CancellationRequested) cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                    operation.State = "running";
                    operation.StartedAtUtc = DateTimeOffset.UtcNow;
                }, cancellation.Token);
                foreach (var item in plan.Items)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    await AuthorizeAsync(owner, cancellation.Token);
                    state = await jobs.ReadAsync(cancellation.Token);
                    var record = MigrationJobStore.RequireOperation(state, owner, id);
                    if (record.Operation.CancellationRequested) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                    if (item.Dependencies.Any(dependency => record.Operation.Items.Single(candidate => candidate.Id == dependency).State != "verified"))
                    {
                        await UpdateItemAsync(owner, id, item.Id, result => { result.State = "not_started"; result.ErrorCode = "migration.dependency_failed"; }, cancellation.Token);
                        continue;
                    }
                    var manifest = await packages.ReadManifestAsync(item.PackageId, cancellation.Token);
                    if (Hash(manifest) != plan.ManifestHashes[item.PackageId]) throw Error("migration.package_changed");
                    var adapter = adapters[item.Kind];
                    var check = await adapter.CheckAsync(item, manifest, plan.Selection, cancellation.Token);
                    if (check.Blockers.Count > 0)
                    {
                        await UpdateItemAsync(owner, id, item.Id, result => { result.State = "blocked"; result.ErrorCode = check.Blockers[0]; }, cancellation.Token);
                        continue;
                    }
                    var credentials = adapter.PrepareCredentials(item, manifest);
                    // Save credentials and the mutation checkpoint BEFORE calling the native service.
                    // A crash can leave a created account; its new password must remain recoverable.
                    var priorCredentials = ReadCredentials(record);
                    priorCredentials.AddRange(credentials);
                    var protectedValue = secrets.Protect(JsonSerializer.Serialize(priorCredentials, Json));
                    await jobs.UpdateAsync(current =>
                    {
                        var active = MigrationJobStore.RequireOperation(current, owner, id);
                        if (active.Operation.CancellationRequested) cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                        active.ProtectedCredentials = protectedValue;
                        active.Operation.CredentialsAvailable = priorCredentials.Count > 0;
                        active.Operation.Items.Single(result => result.Id == item.Id).State = "running";
                    }, cancellation.Token);
                    await AuthorizeAsync(owner, cancellation.Token);
                    MigrationComponentResult outcome;
                    try { outcome = await adapter.ApplyAsync(item, manifest, plan.Selection, credentials, cancellation.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { outcome = new() { PartialStatePossible = true, ErrorCode = SafeError(error, "migration.component_failed") }; }
                    await UpdateItemAsync(owner, id, item.Id, result =>
                    {
                        result.State = outcome.Verified ? "verified" : "failed";
                        result.ErrorCode = outcome.ErrorCode;
                        result.PartialStatePossible = outcome.PartialStatePossible;
                        result.CreatedResources = outcome.CreatedResources;
                        result.Evidence = outcome.Evidence;
                        result.RequiredActions = outcome.RequiredActions;
                    }, CancellationToken.None);
                    // Adapters preserve their partial evidence by returning a cancelled result.
                    // Retain that evidence, then honour cancellation even for the final item.
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                cancellation.Token.ThrowIfCancellationRequested();
                await jobs.UpdateAsync(current =>
                {
                    var operation = MigrationJobStore.RequireOperation(current, owner, id).Operation;
                    operation.RequiredActions.AddRange(operation.Items.SelectMany(item => item.RequiredActions));
                    operation.RequiredActions = operation.RequiredActions.Distinct(StringComparer.Ordinal).ToList();
                    operation.State = operation.Items.All(item => item.State == "verified")
                        ? (operation.RequiredActions.Count > 0 ? "completed_with_actions" : "completed") : "partial";
                    operation.FinishedAtUtc = DateTimeOffset.UtcNow;
                }, CancellationToken.None);
            }
            catch (Exception error)
            {
                await jobs.UpdateAsync(current =>
                {
                    var operation = MigrationJobStore.RequireOperation(current, owner, id).Operation;
                    operation.State = error is OperationCanceledException ? (stoppingToken.IsCancellationRequested ? "interrupted" : "cancelled") : "failed";
                    operation.RequiredActions.Add(SafeError(error, error is OperationCanceledException ? "migration.stopped_review_required" : "migration.operation_failed"));
                    operation.FinishedAtUtc = DateTimeOffset.UtcNow;
                    foreach (var result in operation.Items)
                    {
                        if (result.State == "running") { result.State = "interrupted"; result.PartialStatePossible = true; }
                        else if (result.State == "pending") result.State = "not_started";
                    }
                }, CancellationToken.None);
            }
            finally { Running.TryRemove(id, out _); }
            return true;
        }
        finally { WorkerGate.Release(); }
    }

    private Task UpdateItemAsync(Guid owner, Guid operation, string itemId, Action<MigrationItemResultDto> mutate, CancellationToken ct)
        => jobs.UpdateAsync(state => mutate(MigrationJobStore.RequireOperation(state, owner, operation).Operation.Items.Single(item => item.Id == itemId)), ct);
}
