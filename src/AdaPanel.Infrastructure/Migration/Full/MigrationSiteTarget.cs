using System.Security.Cryptography;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed class MigrationSiteTarget(IIisSiteService sites, ISiteProvisioningService provisioning,
    IMigrationPackageStore packages, IConcurrencyLock gates, IOptions<PanelRuntimeOptions>? runtime = null) : IMigrationTargetAdapter
{
    public string Kind => "site";
    internal Func<string, string> StagePathFactory { get; init; } = target =>
        Path.Combine(Path.GetPathRoot(target)!, "AdaPanel-Migration-" + Guid.NewGuid().ToString("N"));
    internal Func<string, string, FolderWriteAccessProbe.Result> ProbeApplicationRoot { get; init; } = FolderWriteAccessProbe.Probe;

    public async Task<MigrationTargetCheck> CheckAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest,
        MigrationPlanRequest selection, CancellationToken cancellationToken)
    {
        var result = new MigrationTargetCheck();
        var source = manifest.Sites.Single(site => site.Id == item.SourceId);
        var target = TargetPath(item, selection);
        var dataRoot = Environment.ExpandEnvironmentVariables((runtime?.Value ?? new PanelRuntimeOptions()).DataRoot);
        if (PanelWriteTargetGuard.Evaluate(target, dataRoot) != PanelWriteTargetVerdict.Allowed)
        {
            result.Blockers.Add("migration.target_path_forbidden");
            return result;
        }
        PortableMigrationStorage.Guard(target);
        var allSites = await sites.GetMigrationTargetSitesAsync(cancellationToken);
        if (allSites.Any(site => string.IsNullOrWhiteSpace(site.PhysicalPath) && string.IsNullOrWhiteSpace(site.ApplicationRootPath)))
        {
            result.Blockers.Add("migration.private_storage_unverified");
            return result;
        }
        if (allSites.Any(site => site.Name.Equals(item.Target.TargetName, StringComparison.OrdinalIgnoreCase)))
            result.Blockers.Add("migration.target_site_exists");
        if (File.Exists(target) || Directory.Exists(target)) result.Blockers.Add("migration.target_path_exists");
        foreach (var existing in allSites)
        {
            if (new[] { existing.PhysicalPath, existing.ApplicationRootPath }.Where(path => !string.IsNullOrWhiteSpace(path))
                .Any(path => Overlaps(target, path))) result.Blockers.Add("migration.target_path_overlaps_site");
        }
        var hosts = Hosts(source);
        if (hosts.Length == 0) result.Blockers.Add("migration.target_host_required");
        if (source.HostNames.Any(host => Uri.CheckHostName(host) != UriHostNameType.Dns))
            result.Blockers.Add("migration.source_binding_mapping_required");
        if (hosts.Any(host => allSites.SelectMany(site => site.Bindings).Any(binding => binding.Port is 80 or 443
            && binding.Protocol is "http" or "https" && host.Equals(binding.HostName, StringComparison.OrdinalIgnoreCase))))
            result.Blockers.Add("migration.target_binding_conflict");
        if (source.FileCount is null) result.Blockers.Add("migration.source_file_count_unmeasured");
        if (!string.IsNullOrEmpty(source.DocumentRootRelativePath))
        {
            // The existing IIS/PHP/WAF boundary recognizes Laravel's public/ separation.
            // Do not claim a different root layout works while those consumers only see docroot.
            var prefix = source.ContentPath.TrimEnd('/') + "/";
            if (!source.DocumentRootRelativePath.Equals("public", StringComparison.OrdinalIgnoreCase)
                || !manifest.Payloads.Any(payload => payload.Path.Equals(prefix + "artisan", StringComparison.OrdinalIgnoreCase))
                || !manifest.Payloads.Any(payload => payload.Path.Equals(prefix + "composer.json", StringComparison.OrdinalIgnoreCase)))
                result.Blockers.Add("migration.application_root_mapping_required");
        }
        if (item.Target.RuntimeProfile is not ("auto" or "static" or "php" or "aspnet" or "classic-asp" or "mixed" or "node" or "python"))
            result.Blockers.Add("migration.runtime_invalid");
        if (item.Target.RuntimeProfile is "node" or "python")
            result.Warnings.Add("migration.runtime_startup_review_required");
        result.Warnings.Add("migration.site_created_stopped");
        result.Warnings.Add("migration.application_configuration_review_required");
        return result;
    }

    public IReadOnlyList<MigrationCreatedCredential> PrepareCredentials(MigrationPlanItemDto item, PortableMigrationManifest manifest) => [];

    public async Task<MigrationComponentResult> ApplyAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest,
        MigrationPlanRequest selection, IReadOnlyList<MigrationCreatedCredential> credentials, CancellationToken cancellationToken)
    {
        var result = new MigrationComponentResult();
        var source = manifest.Sites.Single(site => site.Id == item.SourceId);
        var target = TargetPath(item, selection);
        var checks = await CheckAsync(item, manifest, selection, cancellationToken);
        if (checks.Blockers.Count > 0) { result.ErrorCode = checks.Blockers[0]; return result; }
        var publicRoots = await MigrationStoragePolicy.PublicRootsAsync(sites, [], cancellationToken);
        // Stage on the TARGET volume, outside all web roots. Directory.Move then atomically
        // claims an absent destination; it cannot merge into an existing directory.
        var stage = StagePathFactory(target);
        PortableMigrationStorage.CreatePrivateDirectory(stage, publicRoots);
        var content = Path.Combine(stage, "content");
        PortableMigrationStorage.CreatePrivateDirectory(content, publicRoots);
        var moved = false;
        try
        {
            var prefix = source.ContentPath.TrimEnd('/') + "/";
            var payloads = manifest.Payloads.Where(payload => payload.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (payloads.Count != source.FileCount) throw Error("migration.source_file_count_mismatch");
            foreach (var empty in source.EmptyDirectories)
                Directory.CreateDirectory(Child(content, empty));
            foreach (var payload in payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Child(content, payload.Path[prefix.Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = await packages.OpenPayloadAsync(item.PackageId, payload.Path, cancellationToken);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[131072];
                long copied = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    copied = checked(copied + count);
                    if (copied > payload.Size) throw Error("migration.payload_changed");
                    hash.AppendData(buffer.AsSpan(0, count));
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                if (copied != payload.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw Error("migration.payload_changed");
            }
            var docroot = string.IsNullOrEmpty(source.DocumentRootRelativePath) ? content : Child(content, source.DocumentRootRelativePath);
            if (!Directory.Exists(docroot)) throw Error("migration.document_root_missing");
            // Re-check after long file copies, before publishing any new destination content.
            checks = await CheckAsync(item, manifest, selection, cancellationToken);
            if (checks.Blockers.Count > 0) { result.ErrorCode = checks.Blockers[0]; return result; }
            PortableMigrationStorage.Guard(Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(content, target);
            moved = true;
            result.PartialStatePossible = true;
            result.CreatedResources.Add("directory:" + target);
            using var creation = await SiteMutationGuard.WatchCreationAsync(gates, item.Target.TargetName, cancellationToken);
            var hosts = Hosts(source);
            var site = await provisioning.CreateAndProvisionAsync(new()
            {
                Name = item.Target.TargetName, PhysicalPath = string.IsNullOrEmpty(source.DocumentRootRelativePath) ? target : Child(target, source.DocumentRootRelativePath),
                HostName = hosts[0], MigrationHostNames = hosts, MigrationFreshOnly = true,
                RuntimeProfile = item.Target.RuntimeProfile, AutoStart = false, ReplaceExisting = false,
                ProvisionDefaultServices = false, InstallRuntimeIfMissing = false,
                CreateDatabase = false, CreateMailDomain = false, CreateFtpUser = false
            }, null, null, cancellationToken);
            result.CreatedResources.Add("site:" + site.Name);
            using var pinned = await creation.CaptureCreatedAsync(sites, site, cancellationToken);
            using var reservation = await pinned.ReserveAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                || !Path.GetFullPath(site.ApplicationRootPath).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                throw Error("migration.application_root_unverified");
            var permission = await provisioning.RepairPermissionsAsync(site.Name,
                (_, ct) => pinned.ValidateAsync(ct), cancellationToken);
            if (!permission.IsGranted) throw Error("migration.site_permissions_unverified");
            if (ProbeApplicationRoot(target, "IIS AppPool\\" + site.ApplicationPoolName).State != FolderWriteAccessProbe.Access.Granted)
                throw Error("migration.application_root_permissions_unverified");
            await pinned.RunAsync(async current =>
            {
                var actual = await sites.GetSiteAsync(current.Name, cancellationToken);
                if (actual.AutoStart || !actual.State.Equals("Stopped", StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFullPath(actual.PhysicalPath).Equals(Path.GetFullPath(site.PhysicalPath), StringComparison.OrdinalIgnoreCase)
                    || hosts.Any(host => !actual.Bindings.Any(binding => binding.Protocol == "http" && binding.Port == 80
                        && binding.HostName.Equals(host, StringComparison.OrdinalIgnoreCase)))) throw Error("migration.site_verification_failed");
            }, cancellationToken);
            result.Verified = true;
            result.PartialStatePossible = false;
            result.Evidence.Add("migration.site_files_hash_verified:" + payloads.Count);
            result.Evidence.Add("migration.site_stopped_bindings_verified");
            result.RequiredActions.AddRange(["migration.database_connection_update_required", "migration.ssl_and_web_response_verification_required", "migration.site_start_after_cutover"]);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            result.PartialStatePossible = moved;
            result.ErrorCode = error is AppException app ? app.MessageKey : "migration.site_import_failed";
            return result;
        }
        finally
        {
            // Only the generated staging directory is cleaned. A partially published target is
            // retained with its journal entry; a retry must use a fresh name/path or explicit recovery.
            if (Directory.Exists(stage)) PortableMigrationStorage.DeleteOwnedDirectory(stage);
        }
    }

    private static string TargetPath(MigrationPlanItemDto item, MigrationPlanRequest selection)
    {
        var root = Path.GetFullPath(selection.TargetBasePath).TrimEnd('\\', '/');
        var path = Path.GetFullPath(item.Target.TargetPath);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path)
            || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Error("migration.target_path_invalid");
        return path;
    }
    private static string Child(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (Path.IsPathRooted(relative) || !path.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Error("migration.payload_path_invalid");
        PortableMigrationStorage.Guard(path);
        return path;
    }
    private static string[] Hosts(PortableMigrationSite source) => source.HostNames
        .Where(host => Uri.CheckHostName(host) == UriHostNameType.Dns)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(host => host.Equals(source.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
    private static bool Overlaps(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(right)) return false;
        left = Path.GetFullPath(left).TrimEnd('\\', '/');
        right = Path.GetFullPath(right).TrimEnd('\\', '/');
        return left.Equals(right, StringComparison.OrdinalIgnoreCase) || left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static AppException Error(string code) => new(code, 409, 409);
}
