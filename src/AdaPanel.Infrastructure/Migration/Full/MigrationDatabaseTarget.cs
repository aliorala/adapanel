using System.Security.Cryptography;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Migration.Full;

/// <summary>Connects immutable payloads to the fresh-only, database-scoped restore boundary.</summary>
public sealed class MigrationDatabaseTarget(IMigrationDatabaseImporter importer, IMigrationPackageStore packages,
    ISiteDatabaseLinkService links, IIisSiteService sites, IConcurrencyLock gates) : IMigrationTargetAdapter
{
    public string Kind => "database";

    public async Task<MigrationTargetCheck> CheckAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest,
        MigrationPlanRequest selection, CancellationToken cancellationToken)
    {
        var source = manifest.Databases.Single(database => database.Id == item.SourceId);
        var result = new MigrationTargetCheck();
        // Native backups include CREATE DATABASE / USE; vendor dumps may also contain
        // qualified view/routine names. A new default database does not rewrite SQL scope.
        // Do not create an empty target before discovering that a rename cannot be restored.
        if (!source.Name.Equals(item.Target.TargetName, StringComparison.Ordinal))
            result.Blockers.Add("migration.database_rename_unsupported");
        if (source.Engine is not ("mysql" or "mariadb"))
            result.Blockers.Add("migration.database_engine_unsupported");
        if (source.Format != "sql" || string.IsNullOrEmpty(source.DumpPath)
            || manifest.Payloads.Count(payload => payload.Path == source.DumpPath) != 1)
            result.Blockers.Add("migration.database_dump_invalid");
        var link = ResolveSite(item, source, manifest, selection);
        if (link.Error.Length > 0) result.Blockers.Add(link.Error);
        if (result.Blockers.Count > 0) return result;
        var plan = await importer.PlanAsync(Request(item, source), cancellationToken);
        result.Blockers.AddRange(plan.Blockers);
        if (!plan.Supported && result.Blockers.Count == 0) result.Blockers.Add("migration.database_engine_unsupported");
        if ((await links.GetAllAsync(cancellationToken)).Any(existing =>
            DatabaseResourceIdentity.Same(existing.Engine, existing.DatabaseName, source.Engine, item.Target.TargetName)))
            result.Blockers.Add("migration.target_database_link_exists");
        result.Warnings.Add("migration.database_new_credentials_required");
        result.Warnings.Add("migration.database_application_settings_review_required");
        result.Warnings.Add("migration.database_source_counts_unmeasured");
        return result;
    }

    public IReadOnlyList<MigrationCreatedCredential> PrepareCredentials(MigrationPlanItemDto item, PortableMigrationManifest manifest) =>
    [new() { ItemId = item.Id, Kind = Kind, ResourceName = item.Target.TargetName,
        UserName = item.Target.DatabaseUserName, Password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)) + "!aA1", Host = "localhost" }];

    public async Task<MigrationComponentResult> ApplyAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest,
        MigrationPlanRequest selection, IReadOnlyList<MigrationCreatedCredential> credentials, CancellationToken cancellationToken)
    {
        var result = new MigrationComponentResult();
        var source = manifest.Databases.Single(database => database.Id == item.SourceId);
        string? temporary = null;
        SiteMutationGuard.Target? pinnedSite = null;
        try
        {
            var checks = await CheckAsync(item, manifest, selection, cancellationToken);
            if (checks.Blockers.Count > 0) { result.ErrorCode = checks.Blockers[0]; return result; }
            var credential = credentials.SingleOrDefault(value => value.ItemId == item.Id && value.Kind == Kind);
            if (credential is null || credential.UserName != item.Target.DatabaseUserName
                || credential.ResourceName != item.Target.TargetName || string.IsNullOrEmpty(credential.Password))
                throw Error("migration.database_credentials_missing");
            var link = ResolveSite(item, source, manifest, selection);
            if (link.Name.Length > 0)
            {
                pinnedSite = await SiteMutationGuard.CaptureAsync(sites, gates, link.Name, cancellationToken);
                var actual = await sites.GetSiteAsync(link.Name, cancellationToken);
                if (!Path.GetFullPath(actual.ApplicationRootPath).Equals(Path.GetFullPath(link.Path), StringComparison.OrdinalIgnoreCase))
                    throw Error("migration.target_site_changed");
                await pinnedSite.ValidateAsync(cancellationToken);
            }
            var payload = manifest.Payloads.Single(value => value.Path == source.DumpPath);
            temporary = SecureTempFile.BuildPath("adapanel-migration-database", ".sql");
            await using (var input = await packages.OpenPayloadAsync(item.PackageId, source.DumpPath, cancellationToken))
            await using (var output = SecureTempFile.Create(temporary))
            {
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
            if (pinnedSite is not null) await pinnedSite.ValidateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // An acknowledgement may be lost after native CREATE. Never retry this dump automatically.
            result.PartialStatePossible = true;
            var imported = await importer.ImportAsync(Request(item, source, temporary, credential.Password), cancellationToken);
            result.CreatedResources.AddRange(imported.CreatedResources);
            result.PartialStatePossible = imported.PartialStatePossible || imported.Imported || imported.AttemptedResources.Count > 0;
            if (!imported.Imported || !imported.Verified || imported.ErrorCode.Length > 0)
            {
                result.ErrorCode = imported.ErrorCode.Length > 0 ? imported.ErrorCode : "migration.database_verification_failed";
                return result;
            }
            if (pinnedSite is not null)
            {
                // The importer has released its resource lease. Take it before the IIS gate,
                // matching deletion/cascade ordering; never hold it while calling ImportAsync.
                using var databaseLease = await DatabaseLifecycle.EnterResourceAsync(source.Engine, item.Target.TargetName, cancellationToken);
                await pinnedSite.RunAsync(async current =>
                {
                    if ((await links.GetAllAsync(cancellationToken)).Any(existing => DatabaseResourceIdentity.Same(
                        existing.Engine, existing.DatabaseName, source.Engine, item.Target.TargetName)))
                        throw Error("migration.target_database_link_exists");
                    await links.LinkAsync(source.Engine, item.Target.TargetName, current.Name, cancellationToken);
                    if (!(await links.GetForSiteAsync(current.Name, cancellationToken)).Any(existing => DatabaseResourceIdentity.Same(
                        existing.Engine, existing.DatabaseName, source.Engine, item.Target.TargetName)))
                        throw Error("migration.database_site_link_unverified");
                }, cancellationToken);
                result.Evidence.Add("migration.database_site_link_verified");
            }
            result.Verified = true;
            result.PartialStatePossible = false;
            result.Evidence.Add("migration.database_payload_hash_verified");
            result.Evidence.Add("migration.database_scoped_import_and_login_verified");
            result.Evidence.Add("migration.database_target_objects:" + imported.ObjectInventory.Count);
            result.RequiredActions.AddRange(["migration.database_application_settings_review_required", "migration.database_source_counts_unmeasured"]);
            return result;
        }
        catch (OperationCanceledException) { result.ErrorCode = "migration.database_import_cancelled"; return result; }
        catch (Exception error) { result.ErrorCode = error is AppException app ? app.MessageKey : "migration.database_import_failed"; return result; }
        finally
        {
            pinnedSite?.Dispose();
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static MigrationDatabaseImportRequest Request(MigrationPlanItemDto item, PortableMigrationDatabase source,
        string path = "", string password = "") => new()
    {
        Engine = source.Engine, TargetDatabase = item.Target.TargetName, TargetUserName = item.Target.DatabaseUserName,
        DumpPath = path, TargetPassword = password, AdaptDefiners = true
    };

    private static (string Name, string Path, string Error) ResolveSite(MigrationPlanItemDto item, PortableMigrationDatabase source,
        PortableMigrationManifest manifest, MigrationPlanRequest selection)
    {
        var selected = manifest.Sites.Where(site => selection.AllSites || selection.SiteIds.Contains(MigrationResourceKey.Create(item.PackageId, site.Id), StringComparer.Ordinal)).ToList();
        var key = item.Target.LinkedSiteId;
        if (key.Length == 0)
        {
            var related = selected.Where(site => source.SiteIds.Contains(site.Id, StringComparer.Ordinal)).ToList();
            if (related.Count > 1) return ("", "", "migration.database_site_mapping_required");
            if (related.Count == 0) return ("", "", "");
            key = MigrationResourceKey.Create(item.PackageId, related[0].Id);
        }
        var linked = selected.SingleOrDefault(site => MigrationResourceKey.Create(item.PackageId, site.Id) == key);
        if (linked is null) return ("", "", "migration.database_site_not_selected");
        var mapping = selection.Mappings.SingleOrDefault(value => value.ResourceId == key);
        var name = string.IsNullOrWhiteSpace(mapping?.TargetName) ? linked.Name : mapping.TargetName;
        return (name, string.IsNullOrWhiteSpace(mapping?.TargetPath) ? Path.Combine(selection.TargetBasePath, name) : mapping.TargetPath, "");
    }

    private static AppException Error(string key) => new(key, 409, 409);
}
