using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Infrastructure.Migration.Packages;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed partial class MigrationAcquisitionService
{
    private long TotalPayloadLimit => Math.Min(packageLimits.Value.MaxTotalUncompressedBytes, liveLimits.Value.MaxTotalArtifactBytes);
    private string PrivateRoot => Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(runtime.Value.DataRoot), "migration-private"));
    private string JobRoot(Guid id) => Path.Combine(PrivateRoot, "acquire", id.ToString("N"));
    private async Task<string[]> ActualPublicRootsAsync(CancellationToken ct)
    {
        var inventory = await sites.GetMigrationTargetSitesAsync(ct);
        if (inventory is null || inventory.Any(site => string.IsNullOrWhiteSpace(site.PhysicalPath) && string.IsNullOrWhiteSpace(site.ApplicationRootPath)))
            throw Error("migration.acquisition_storage_unverified");
        return inventory.SelectMany(site => new[] { site.PhysicalPath, site.ApplicationRootPath }).Where(path => !string.IsNullOrWhiteSpace(path))
            .Concat(packageLimits.Value.PublicWebRoots).Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static void EnsureOutsidePublicRoots(string candidate, IEnumerable<string> publicRoots)
    {
        candidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var root in publicRoots)
        {
            var web = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (candidate.Equals(web, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || web.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Error("migration.acquisition_storage_unverified");
        }
        PortableMigrationStorage.Guard(candidate);
    }
    private async Task EnsurePrivateStorageAsync(CancellationToken ct)
    {
        if (liveLimits.Value.MaxTotalArtifactBytes <= 0 || TotalPayloadLimit <= 0) throw Error("migration.acquisition_budget_exceeded");
        var roots = await ActualPublicRootsAsync(ct);
        foreach (var path in new[] { PrivateRoot, Path.Combine(Environment.ExpandEnvironmentVariables(runtime.Value.DataRoot), "migration-packages"),
            Path.Combine(Environment.ExpandEnvironmentVariables(runtime.Value.DataRoot), "migration-sources"), Path.Combine(Environment.ExpandEnvironmentVariables(runtime.Value.DataRoot), "migration-normalization") })
            EnsureOutsidePublicRoots(path, roots);
        PortableMigrationStorage.CreatePrivateDirectory(PrivateRoot, roots);
    }
    private async Task<string> CreateUnitWorkspaceAsync(Guid owner, Guid id, string sourceUnit, CancellationToken ct)
    {
        var roots = await ActualPublicRootsAsync(ct);
        var root = JobRoot(id); EnsureOutsidePublicRoots(root, roots);
        PortableMigrationStorage.CreatePrivateDirectory(root, roots);
        var unit = Path.Combine(root, Guid.NewGuid().ToString("N"));
        PortableMigrationStorage.CreatePrivateDirectory(unit, roots);
        await jobs.UpdateAsync(state =>
        {
            var record = MigrationJobStore.RequireAcquisition(state, owner, id);
            record.LocalWorkspaces.Add(unit); record.LocalWorkspaceUnits.Add(unit, sourceUnit);
        }, ct);
        return unit;
    }
    private string ValidateWorkspace(Guid id, string workspace)
    {
        var path = Path.GetFullPath(workspace);
        var relative = Path.GetRelativePath(JobRoot(id), path);
        if (!Regex.IsMatch(relative, @"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant)) throw Error("migration.acquisition_storage_unverified");
        PortableMigrationStorage.Guard(path); return path;
    }
    private async Task CleanupUnitAsync(Guid owner, Guid id, string workspace, CancellationToken ct)
    {
        try
        {
            workspace = ValidateWorkspace(id, workspace);
            EnsureOutsidePublicRoots(workspace, await ActualPublicRootsAsync(ct));
            if (Directory.Exists(workspace)) PortableMigrationStorage.DeleteOwnedDirectory(workspace);
            await jobs.UpdateAsync(state =>
            {
                var record = MigrationJobStore.RequireAcquisition(state, owner, id);
                record.LocalWorkspaces.Remove(workspace); record.LocalWorkspaceUnits.Remove(workspace);
            }, ct);
        }
        catch
        {
            await jobs.UpdateAsync(state => AddWarning(MigrationJobStore.RequireAcquisition(state, owner, id).Acquisition,
                "migration.acquisition_local_recovery_retained"), CancellationToken.None);
        }
    }
    private async Task<FileStream> OpenVerifiedSourceAsync(Guid owner, Guid id, string workspace, string unit, string provider,
        LiveSourceArtifact artifact, CancellationToken ct)
    {
        workspace = ValidateWorkspace(id, workspace);
        if (!artifact.Full || artifact.Provider != provider || artifact.SourceUnitId != unit || artifact.Bytes <= 0
            || artifact.Bytes > liveLimits.Value.MaxArtifactBytes || artifact.Bytes > packageLimits.Value.MaxUploadBytes
            || artifact.Sha256 is null || !Regex.IsMatch(artifact.Sha256, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant)
            || provider == "cpanel" && artifact.Format != "cpanel-cpmove" || provider == "plesk" && artifact.Format != "plesk-backup-tar") throw Error("migration.acquisition_source_invalid");
        var path = Path.GetFullPath(artifact.ServerGeneratedPath);
        if (!path.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetRelativePath(workspace, path).Contains(Path.DirectorySeparatorChar)) throw Error("migration.acquisition_source_invalid");
        PortableMigrationStorage.Guard(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != artifact.Bytes) throw Error("migration.acquisition_source_invalid");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw Error("migration.acquisition_source_invalid");
            await jobs.UpdateAsync(state =>
            {
                var acquisition = MigrationJobStore.RequireAcquisition(state, owner, id).Acquisition;
                var prior = acquisition.DownloadedBytes ?? 0;
                if (artifact.Bytes > liveLimits.Value.MaxTotalArtifactBytes - prior) throw Error("migration.acquisition_budget_exceeded");
                acquisition.DownloadedBytes = prior + artifact.Bytes;
            }, ct);
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    // Returned package IDs come only from injected server-side connectors/normalizers. Journal before
    // the first cancellable checkpoint so a rejected artifact remains discoverable for an operator.
    private async Task JournalCandidateAsync(Guid owner, Guid id, PortableMigrationPackage package, string provider, string unit)
    {
        using var journalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await jobs.UpdateAsync(state =>
        {
            if (package.Id == Guid.Empty || state.Packages.Any(item => item.PackageId == package.Id)
                || state.Acquisitions.Any(item => item.CandidatePackages.Any(candidate => candidate.PackageId == package.Id)
                    || item.PendingPackageIds.Contains(package.Id) || item.Acquisition.PackageIds.Contains(package.Id)))
                throw Error("migration.acquisition_package_invalid");
            var record = MigrationJobStore.RequireAcquisition(state, owner, id);
            record.CandidatePackages.Add(new() { PackageId = package.Id, Provider = provider, SourceUnitId = unit });
        }, journalTimeout.Token);
    }
    private static void PublishCandidateRecoveries(OwnedMigrationAcquisition record)
    {
        record.Acquisition.PackageRecoveries = record.CandidatePackages.ToList();
        if (record.CandidatePackages.Count > 0) AddWarning(record.Acquisition, "migration.acquisition_unregistered_package_recovery_required");
    }

    private sealed record ExpectedNativeScope(HashSet<string> Sites, HashSet<string> Databases, HashSet<string> Tasks);
    private static ExpectedNativeScope NativeScope(MigrationSourceInventoryDto inventory, MigrationExportRequest selection)
    {
        if (inventory.Provider != "adapanel") throw Error("migration.acquisition_scope_changed");
        HashSet<string> Select(IEnumerable<string> available, bool all, string[] selected)
        {
            var ids = available.ToHashSet(StringComparer.Ordinal);
            if (selected.Any(id => !ids.Contains(id))) throw Error("migration.acquisition_scope_changed");
            return all ? ids : selected.ToHashSet(StringComparer.Ordinal);
        }
        return new(Select(inventory.Sites.Select(item => item.Id), selection.AllSites, selection.SiteIds),
            Select(inventory.Databases.Select(item => item.Id), selection.AllDatabases, selection.DatabaseIds),
            selection.IncludeTasks ? Select(inventory.Tasks.Select(item => item.Id), selection.TaskIds.Length == 0, selection.TaskIds) : []);
    }
    private async Task AcceptPackageAsync(Guid owner, Guid id, PortableMigrationPackage package, string provider, string unit,
        ExpectedNativeScope? expected, MigrationExportRequest? selection, CancellationTokenSource cancellation)
    {
        await SetStage(owner, id, "verifying-package", cancellation.Token);
        if (package.Id == Guid.Empty) throw Error("migration.acquisition_package_invalid");
        var manifest = await packages.ReadManifestAsync(package.Id, cancellation.Token);
        var hash = Hash(manifest);
        if (hash != Hash(package.Manifest) || manifest.Provider.ToString().ToLowerInvariant() != provider) throw Error("migration.acquisition_package_changed");
        if (provider is "cpanel" or "plesk" && manifest.SourceName != unit[(provider.Length + 1)..]) throw Error("migration.acquisition_scope_changed");
        if (expected is not null)
        {
            if (!expected.Sites.SetEquals(manifest.Sites.Select(item => item.Id)) || !expected.Databases.SetEquals(manifest.Databases.Select(item => item.Id))
                || !expected.Tasks.SetEquals(manifest.Tasks.Select(item => item.Id)) || selection is null
                || !selection.IncludeMail && manifest.MailDomains.Count != 0 || !selection.IncludeDns && manifest.DnsZones.Count != 0
                || manifest.MailDomains.Any(domain => domain.SiteIds.Count == 0 || domain.SiteIds.Any(site => !expected.Sites.Contains(site)))
                || manifest.DnsZones.Any(zone => zone.SiteIds.Count == 0 || zone.SiteIds.Any(site => !expected.Sites.Contains(site)))) throw Error("migration.acquisition_scope_changed");
        }
        long total = 0;
        foreach (var payload in manifest.Payloads)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (payload.Size < 0 || payload.Size > TotalPayloadLimit - total) throw Error("migration.acquisition_budget_exceeded");
            await using var stream = await packages.OpenPayloadAsync(package.Id, payload.Path, cancellation.Token);
            if (stream.Length != payload.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation.Token)).Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Error("migration.acquisition_package_changed");
            total += payload.Size;
        }
        if (Hash(await packages.ReadManifestAsync(package.Id, cancellation.Token)) != hash) throw Error("migration.acquisition_package_changed");
        await CheckpointAsync(owner, id, cancellation);
        await jobs.UpdateAsync(state =>
        {
            var record = MigrationJobStore.RequireAcquisition(state, owner, id);
            if (record.Acquisition.PackageIds.Contains(package.Id) || record.PendingPackageIds.Contains(package.Id)
                || state.Packages.Any(p => p.PackageId == package.Id)) throw Error("migration.acquisition_package_invalid");
            if (total > TotalPayloadLimit - record.Acquisition.NormalizedPayloadBytes) throw Error("migration.acquisition_budget_exceeded");
            record.Acquisition.NormalizedPayloadBytes += total;
            record.CandidatePackages.Single(candidate => candidate.PackageId == package.Id).Verified = true;
            record.VerifiedPackageHashes[package.Id] = hash; record.PendingPackageIds.Add(package.Id);
        }, cancellation.Token);
        await CheckpointAsync(owner, id, cancellation);
        await SetStage(owner, id, "registering", cancellation.Token);
        await full.RegisterPackagesAsync(owner, [package.Id], cancellation.Token);
        await jobs.UpdateAsync(state => ReconcileRegistrations(state, MigrationJobStore.RequireAcquisition(state, owner, id)), CancellationToken.None);
        var registered = MigrationJobStore.RequireAcquisition(await jobs.ReadAsync(CancellationToken.None), owner, id);
        if (!registered.Acquisition.PackageIds.Contains(package.Id)) throw Error("migration.acquisition_package_invalid");
    }

    private static MigrationAcquisitionRecoveryDto? ValidRecovery(string unit, string workspace, IEnumerable<string> paths, bool confirmed, bool cleaned)
    {
        if (workspace.Length == 0) return null;
        if (!Regex.IsMatch(workspace, @"\A/var/tmp/adapanel-migration-[a-f0-9]{32}\z", RegexOptions.CultureInvariant)) throw Error("migration.acquisition_source_invalid");
        var files = paths.ToList();
        if (files.Count > 1000 || files.Distinct(StringComparer.Ordinal).Count() != files.Count
            || files.Any(path => !path.StartsWith(workspace + "/", StringComparison.Ordinal)
                || path[(workspace.Length + 1)..] is "." or ".."
                || !Regex.IsMatch(path[(workspace.Length + 1)..], @"\A[a-zA-Z0-9_.-]{1,255}\z", RegexOptions.CultureInvariant))) throw Error("migration.acquisition_source_invalid");
        return new() { SourceUnitId = unit, Workspace = workspace, Files = files, ProcessCompletionConfirmed = confirmed, CleanupComplete = cleaned };
    }
    private async Task SaveRecoveryAsync(Guid owner, Guid id, string unit, string localWorkspace, LiveMigrationAcquireResult result, CancellationToken ct)
    {
        ValidateWorkspace(id, localWorkspace);
        var recovery = ValidRecovery(unit, result.RemoteWorkspace, result.AttemptedRemoteFiles, result.ProcessCompletionConfirmed, result.RemoteCleanupComplete);
        if (result.Artifacts.Count > 0 && recovery is null) throw Error("migration.acquisition_source_invalid");
        await jobs.UpdateAsync(state =>
        {
            var status = MigrationJobStore.RequireAcquisition(state, owner, id).Acquisition;
            if (recovery is not null)
            {
                var prior = status.RemoteRecoveries.SingleOrDefault(r => r.Workspace == recovery.Workspace);
                if (prior is not null) status.RemoteRecoveries.Remove(prior);
                status.RemoteRecoveries.Add(recovery);
            }
            RefreshRecoverySummary(status);
            AddWarning(status, "migration.live_snapshot_not_atomic");
        }, ct);
    }
    private async Task RecoverLocalJournalsAsync(Guid owner, Guid id, CancellationToken ct)
    {
        try
        {
            var record = MigrationJobStore.RequireAcquisition(await jobs.ReadAsync(ct), owner, id);
            foreach (var workspace in record.LocalWorkspaces)
            {
                ValidateWorkspace(id, workspace);
                if (!Directory.Exists(workspace)) continue;
                var count = 0;
                foreach (var path in Directory.EnumerateFiles(workspace, "live-source-recovery-*.json", SearchOption.TopDirectoryOnly))
                {
                    if (++count > 1000) throw Error("migration.acquisition_source_invalid");
                    PortableMigrationStorage.Guard(path);
                    if (new FileInfo(path).Length > 1024 * 1024) throw Error("migration.acquisition_source_invalid");
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    var root = document.RootElement;
                    var remote = root.GetProperty("RemoteWorkspace").GetString() ?? "";
                    var planned = root.GetProperty("PlannedFiles").EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
                    var sourceUnit = record.LocalWorkspaceUnits.GetValueOrDefault(workspace, "");
                    if (!record.Acquisition.SourceUnitIds.Contains(sourceUnit, StringComparer.Ordinal)) sourceUnit = "";
                    var recovery = ValidRecovery(sourceUnit, remote, planned, confirmed: false, cleaned: false);
                    if (recovery is null) continue;
                    await jobs.UpdateAsync(state =>
                    {
                        var status = MigrationJobStore.RequireAcquisition(state, owner, id).Acquisition;
                        if (!status.RemoteRecoveries.Any(r => r.Workspace == recovery.Workspace)) status.RemoteRecoveries.Add(recovery);
                        RefreshRecoverySummary(status);
                    }, ct);
                }
            }
        }
        catch
        {
            await jobs.UpdateAsync(state => AddWarning(MigrationJobStore.RequireAcquisition(state, owner, id).Acquisition,
                "migration.acquisition_recovery_metadata_unverified"), CancellationToken.None);
        }
    }
    private static void RefreshRecoverySummary(MigrationAcquisitionDto status)
    {
        status.RemoteCleanupComplete = status.RemoteRecoveries.Count > 0 && status.RemoteRecoveries.All(r => r.CleanupComplete);
        status.RemoteRecoveryWorkspace = status.RemoteRecoveries.FirstOrDefault(r => !r.CleanupComplete)?.Workspace ?? "";
        status.RemoteRecoveryFiles = status.RemoteRecoveries.Where(r => !r.CleanupComplete).SelectMany(r => r.Files).Distinct(StringComparer.Ordinal).ToList();
        if (status.RemoteRecoveries.Any(r => !r.CleanupComplete)) AddWarning(status, "migration.acquisition_remote_cleanup_required");
    }
    private static void AddWarning(MigrationAcquisitionDto status, string warning)
    { if (!status.Warnings.Contains(warning, StringComparer.Ordinal)) status.Warnings.Add(warning); }
}
