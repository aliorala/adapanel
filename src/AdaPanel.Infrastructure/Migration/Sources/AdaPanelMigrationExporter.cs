using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Migration.Sources;

/// <summary>Exports selected data without creating a retained backup or changing source services.</summary>
public sealed class AdaPanelMigrationExporter(
    IIisSiteService sites, IDatabaseService databases, IMailService mail,
    IMailAccountArchiver mailArchives, IDnsService dns, ISchedulerService scheduler,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    IOptions<PortableMigrationPackageOptions> packageOptions) : IAdaPanelMigrationExporter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MigrationSourceInventoryDto> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<PortableMigrationIssue>();
        var sourceSites = new List<PortableMigrationSite>();
        var sourceDatabases = new List<PortableMigrationDatabase>();
        var sourceTasks = new List<PortableMigrationTask>();
        try
        {
            foreach (var site in await sites.GetSitesAsync(cancellationToken))
                sourceSites.Add(SiteMetadata(site));
        }
        catch (OperationCanceledException) { throw; }
        catch { errors.Add(Issue("sites", "migration.export.sites_unreadable")); }

        try
        {
            foreach (var engine in await databases.GetEnginesAsync(cancellationToken))
            {
                if (!engine.Installed) continue;
                if (!engine.Ready)
                {
                    errors.Add(Issue("databases:" + engine.Engine, "migration.export.database_access_required"));
                    continue;
                }
                try
                {
                    var workspace = await databases.GetWorkspaceAsync(engine.Engine, cancellationToken);
                    foreach (var database in workspace.Databases)
                    {
                        // Workspace includes administrative connection data: project only selected metadata.
                        var detail = await databases.GetDetailAsync(engine.Engine, database.Name, cancellationToken);
                        if (detail.SiteName is null)
                            errors.Add(Issue(Id("db", engine.Engine + ":" + database.Name), "migration.export.database_site_unknown"));
                        sourceDatabases.Add(new()
                        {
                            Id = Id("db", engine.Engine + ":" + database.Name), Name = database.Name,
                            Engine = engine.Engine, Charset = database.Encoding, Collation = database.Collation,
                            Format = IsSqlServer(engine.Engine) ? "bak" : "sql",
                            SiteIds = sourceSites.Where(site => site.Name.Equals(detail.SiteName, StringComparison.OrdinalIgnoreCase))
                                .Select(site => site.Id).ToList()
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { errors.Add(Issue("databases:" + engine.Engine, "migration.export.databases_unreadable")); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { errors.Add(Issue("databases", "migration.export.databases_unreadable")); }

        try
        {
            foreach (var task in await scheduler.GetTasksAsync(cancellationToken))
                sourceTasks.Add(TaskMetadata(task, sourceSites));
        }
        catch (OperationCanceledException) { throw; }
        catch { errors.Add(Issue("tasks", "migration.export.tasks_unreadable")); }

        return new()
        {
            MeasuredAtUtc = DateTimeOffset.UtcNow, Complete = errors.Count == 0,
            Sites = sourceSites, Databases = sourceDatabases, Tasks = sourceTasks, Issues = errors
        };
    }

    public async Task<MigrationExportArtifact> ExportAsync(MigrationExportRequest request, CancellationToken cancellationToken = default)
    {
        var inventory = await GetInventoryAsync(cancellationToken);
        var selectedSites = Select(inventory.Sites, request.AllSites, request.SiteIds, item => item.Id);
        var selectedDbs = Select(inventory.Databases, request.AllDatabases, request.DatabaseIds, item => item.Id);
        var selectedTasks = request.IncludeTasks
            ? Select(inventory.Tasks, request.TaskIds.Length == 0, request.TaskIds, item => item.Id) : [];
        var siteIds = selectedSites.Select(site => site.Id).ToHashSet(StringComparer.Ordinal);
        if (selectedSites.Count + selectedDbs.Count + selectedTasks.Count == 0)
            throw Error("migration.export.selection_required");
        if (inventory.Issues.Any(issue => issue.ResourceId == "sites" && (request.AllSites || request.SiteIds.Length > 0)
            || issue.ResourceId.StartsWith("databases", StringComparison.Ordinal) && request.AllDatabases
            || issue.ResourceId == "tasks" && request.IncludeTasks
            || selectedDbs.Any(database => database.Id == issue.ResourceId) && issue.Code != "migration.export.database_site_unknown"))
            throw Error("migration.export.inventory_incomplete");

        // Discovery/status must not expose task arguments or signed URLs. Keep the original
        // command in a private, hashed payload only when this task was explicitly selected.
        List<ScheduledTaskDto> nativeTasks = selectedTasks.Count == 0 ? [] : (await scheduler.GetTasksAsync(cancellationToken)).ToList();

        var nativeSites = await sites.GetMigrationTargetSitesAsync(cancellationToken);
        var allRoots = nativeSites.SelectMany(site => new[] { site.PhysicalPath, site.ApplicationRootPath })
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var workspace = Path.Combine(Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot)),
            "migration-exports", Guid.NewGuid().ToString("N"));
        PortableMigrationStorage.CreatePrivateDirectory(workspace, allRoots);
        var output = Path.Combine(workspace, "migration.zip");
        var manifest = new PortableMigrationManifest
        {
            PackageId = Guid.NewGuid(), Provider = PortableMigrationProvider.Adapanel,
            CreatedAtUtc = DateTimeOffset.UtcNow, SourceName = Environment.MachineName, Complete = true
        };
        var budget = new ExportBudget();
        try
        {
            await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var source in selectedSites)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tree = await AddTreeAsync(archive, manifest, budget, source.SourcePhysicalPath, "sites/" + source.Id + "/files", cancellationToken);
                    manifest.Sites.Add(new()
                    {
                        Id = source.Id, Name = source.Name, SourcePhysicalPath = source.SourcePhysicalPath,
                        ContentPath = "sites/" + source.Id + "/files", RuntimeProfile = source.RuntimeProfile,
                        DocumentRootRelativePath = source.DocumentRootRelativePath, PhpVersion = source.PhpVersion,
                        HostNames = source.HostNames, FileCount = tree.Files, EmptyDirectories = tree.EmptyDirectories
                    });
                }
                foreach (var source in selectedDbs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Path.Combine(workspace, source.Id + "." + source.Format);
                    await databases.ExportDumpAsync(new DatabaseDumpRequest
                    {
                        Engine = source.Engine, DatabaseName = source.Name, FilePath = path, PortableDefiners = true
                    }, cancellationToken);
                    if (new FileInfo(path).Length == 0) throw Error("migration.export.empty_database_dump");
                    var payload = "databases/" + source.Id + "." + source.Format;
                    await AddFileAsync(archive, manifest, budget, path, payload, cancellationToken);
                    manifest.Databases.Add(new()
                    {
                        Id = source.Id, Name = source.Name, Engine = source.Engine,
                        SiteIds = source.SiteIds.Where(siteIds.Contains).ToList(), DumpPath = payload,
                        Format = source.Format, Charset = source.Charset, Collation = source.Collation
                    });
                    File.Delete(path);
                }
                if (request.IncludeMail && selectedSites.Count > 0)
                    await AddMailAsync(archive, manifest, budget, selectedSites, workspace, cancellationToken);
                if (request.IncludeDns)
                {
                    foreach (var site in selectedSites)
                    {
                        var zone = await dns.GetMigrationSnapshotAsync(site.Name, cancellationToken);
                        if (zone.IsSecondary || string.IsNullOrWhiteSpace(zone.ZoneName)
                            || !zone.IsWindowsDnsAvailable || !zone.IsWindowsZonePresent
                            || !zone.Source.Equals("windows-dns", StringComparison.OrdinalIgnoreCase)
                            || !zone.WindowsZoneType.Equals("Primary", StringComparison.OrdinalIgnoreCase)
                            || zone.ModeConfigurationConflict || zone.TransferConfigurationConflict)
                            throw Error("migration.export.dns_zone_unreadable");
                        var existing = manifest.DnsZones.FirstOrDefault(item => item.Name.Equals(zone.ZoneName, StringComparison.OrdinalIgnoreCase));
                        if (existing is not null) { existing.SiteIds.Add(site.Id); continue; }
                        manifest.DnsZones.Add(new()
                        {
                            Id = Id("dns", zone.ZoneName), Name = zone.ZoneName, SiteIds = [site.Id],
                            Records = zone.Records.Select(record => new PortableMigrationDnsRecord
                            { Type = record.Type, Name = record.Host, Value = record.Value, Ttl = record.Ttl, Priority = record.Priority ?? 0 }).ToList()
                        });
                    }
                }
                foreach (var source in selectedTasks)
                {
                    // Task files already inside selected sites are covered by their payloads.
                    // An external working directory requires an explicit artifact root mapping.
                    if (source.SiteIds.Count == 0 || source.SiteIds.Any(id => !siteIds.Contains(id)))
                        throw Error("migration.export.task_files_required");
                    var native = nativeTasks.SingleOrDefault(task => Id("task", task.Name) == source.Id)
                        ?? throw Error("migration.export.selection_changed");
                    var current = TaskMetadata(native, inventory.Sites);
                    if (JsonSerializer.Serialize(current, Json) != JsonSerializer.Serialize(source, Json))
                        throw Error("migration.export.source_changed");
                    var taskFile = Path.Combine(workspace, source.Id + ".json");
                    await File.WriteAllTextAsync(taskFile, JsonSerializer.Serialize(new { format = "adapanel-task-v1", task = native }, Json),
                        new UTF8Encoding(false), cancellationToken);
                    var taskPayload = "tasks/" + source.Id + "/task.json";
                    await AddFileAsync(archive, manifest, budget, taskFile, taskPayload, cancellationToken);
                    File.Delete(taskFile);
                    source.ArtifactPaths.Add(taskPayload);
                    manifest.Tasks.Add(source);
                }
                var json = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
                if (json.Length > packageOptions.Value.MaxManifestBytes) throw Error("migration.export.manifest_limit");
                await using var entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
                await entry.WriteAsync(json, cancellationToken);
            }
            if (new FileInfo(output).Length > packageOptions.Value.MaxUploadBytes) throw Error("migration.export.size_limit");
            cancellationToken.ThrowIfCancellationRequested();
            return new(output, "AdaPanel-migration-" + manifest.CreatedAtUtc.ToString("yyyyMMdd-HHmmss") + ".zip", workspace);
        }
        catch
        {
            PortableMigrationStorage.DeleteOwnedDirectory(workspace);
            throw;
        }
    }

    private async Task AddMailAsync(ZipArchive archive, PortableMigrationManifest manifest, ExportBudget budget,
        List<PortableMigrationSite> selectedSites, string workspace, CancellationToken cancellationToken)
    {
        var overview = await mail.GetWorkspaceAsync(null, cancellationToken);
        if (!overview.Installed) return;
        if (!overview.Configured) throw Error("migration.export.mail_access_required");
        foreach (var domain in overview.Domains)
        {
            var related = selectedSites.Where(site => site.Name.Equals(domain.Name, StringComparison.OrdinalIgnoreCase)
                || site.HostNames.Contains(domain.Name, StringComparer.OrdinalIgnoreCase)).Select(site => site.Id).ToList();
            if (related.Count == 0) continue;
            var details = await mail.GetWorkspaceAsync(domain.Name, cancellationToken);
            if (!details.AccountsMeasured || !details.ListsMeasured) throw Error("migration.export.mail_inventory_incomplete");
            var exported = new PortableMigrationMailDomain
            {
                Id = Id("mail", domain.Name), Name = domain.Name, SiteIds = related, Active = domain.Active,
                Aliases = details.Aliases.Select(alias => new PortableMigrationMailAlias
                { Address = alias.Address, Destination = alias.Target, Active = alias.Active }).ToList(),
                Lists = details.Lists.Select(list => new PortableMigrationMailList
                {
                    Address = list.Address, Description = list.Description, Recipients = list.Recipients.ToList(),
                    Aliases = list.Aliases.ToList(), PostingPolicy = list.PostingPolicy, AllowedSenders = list.AllowedSenders.ToList()
                }).ToList()
            };
            if (details.Accounts.Count > 0)
            {
                var mailDirectory = Path.Combine(workspace, exported.Id);
                var result = await mailArchives.ArchiveDomainAsync(domain.Name, mailDirectory, cancellationToken);
                var expected = details.Accounts.Select(account => account.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (result.Failures.Count != 0 || result.Accounts.Count != expected.Count
                    || !expected.SetEquals(result.Accounts.Select(account => account.Address)))
                    throw Error("migration.export.mail_payload_incomplete");
                foreach (var account in result.Accounts)
                {
                    var info = details.Accounts.Single(item => item.Address.Equals(account.Address, StringComparison.OrdinalIgnoreCase));
                    var payload = "mail/" + exported.Id + "/" + Id("account", account.Address) + ".sqlite";
                    if (new FileInfo(account.FilePath).Length == 0) throw Error("migration.export.mail_payload_incomplete");
                    await AddFileAsync(archive, manifest, budget, account.FilePath, payload, cancellationToken);
                    exported.Accounts.Add(new()
                    {
                        Email = account.Address, QuotaMb = info.MaxSizeMB, Active = info.Active,
                        ArchivePath = payload, ArchiveFormat = "vandelay-sqlite",
                        EmailCount = account.EmailCount >= 0 ? account.EmailCount : null
                    });
                }
            }
            manifest.MailDomains.Add(exported);
        }
    }

    private async Task<(long Files, List<string> EmptyDirectories)> AddTreeAsync(ZipArchive archive,
        PortableMigrationManifest manifest, ExportBudget budget, string root, string prefix, CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(root);
        PortableMigrationStorage.Guard(root);
        if (!Directory.Exists(root)) throw Error("migration.export.source_directory_missing");
        var pending = new Stack<string>(); pending.Push(root);
        long count = 0;
        var empty = new List<string>();
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PortableMigrationStorage.Guard(directory);
            var hadChildren = false;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                hadChildren = true;
                cancellationToken.ThrowIfCancellationRequested();
                if (manifest.Payloads.Count + pending.Count + empty.Count >= packageOptions.Value.MaxItems)
                    throw Error("migration.export.entry_limit");
                PortableMigrationStorage.Guard(path);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Error("migration.export.link_not_portable");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                await AddFileAsync(archive, manifest, budget, path, prefix + "/" + Path.GetRelativePath(root, path).Replace('\\', '/'), cancellationToken);
                count++;
            }
            if (!hadChildren && directory != root) empty.Add(Path.GetRelativePath(root, directory).Replace('\\', '/'));
            if (manifest.Payloads.Count + pending.Count + empty.Count > packageOptions.Value.MaxItems)
                throw Error("migration.export.entry_limit");
        }
        return (count, empty);
    }

    private async Task AddFileAsync(ZipArchive archive, PortableMigrationManifest manifest, ExportBudget budget,
        string path, string payloadPath, CancellationToken cancellationToken)
    {
        PortableMigrationStorage.Guard(path);
        var info = new FileInfo(path);
        var length = info.Length;
        var modified = info.LastWriteTimeUtc;
        if (length > packageOptions.Value.MaxPayloadBytes
            || budget.TotalBytes > packageOptions.Value.MaxTotalUncompressedBytes - length
            || manifest.Payloads.Count >= packageOptions.Value.MaxItems) throw Error("migration.export.size_limit");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await using var output = archive.CreateEntry(payloadPath, CompressionLevel.Optimal).Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            copied += read;
            if (copied > length) throw Error("migration.export.source_changed");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        info.Refresh();
        if (copied != length || info.LastWriteTimeUtc != modified) throw Error("migration.export.source_changed");
        manifest.Payloads.Add(new() { Path = payloadPath, Size = copied, Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() });
        budget.TotalBytes += copied;
    }

    private static PortableMigrationSite SiteMetadata(SiteDto site)
    {
        // IIS PhysicalPath may point at Laravel's public/ directory; the application
        // root contains .env, vendor and the actual application and must travel too.
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        var relative = Path.GetRelativePath(root, site.PhysicalPath);
        if (relative == ".") relative = "";
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw Error("migration.export.document_root_outside_site");
        return new()
        {
            Id = Id("site", site.Name), Name = site.Name, SourcePhysicalPath = root,
            DocumentRootRelativePath = relative.Replace('\\', '/'), RuntimeProfile = "auto",
            HostNames = site.Bindings.Where(binding => binding.Protocol is "http" or "https")
                .Select(binding => binding.HostName).Where(host => !string.IsNullOrWhiteSpace(host)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static PortableMigrationTask TaskMetadata(ScheduledTaskDto task, List<PortableMigrationSite> sourceSites) => new()
    {
        Id = Id("task", task.Name), Name = task.Name, Kind = task.TaskType,
        WorkingDirectory = task.WorkingDirectory, SourcePlatform = "windows",
        ScheduleType = task.ScheduleType, Interval = task.Interval, TimeOfDay = task.TimeOfDay,
        ScheduleValue = task.RepetitionInterval, RunAs = task.RunAs, TimeLimitHours = task.TimeLimitHours,
        PhpVersion = task.PhpVersion, ScheduleUnknown = task.ScheduleUnknown,
        SiteIds = sourceSites.Where(site => IsInside(site.SourcePhysicalPath, task.WorkingDirectory)
            || IsInside(site.SourcePhysicalPath, task.Command)).Select(site => site.Id).ToList()
    };

    private static bool IsInside(string root, string path) => !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && (string.Equals(PathContainment.Normalize(root)?.TrimEnd('\\', '/'),
            PathContainment.Normalize(path)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || PathContainment.IsInsideRoot(root, path));
    private static bool IsSqlServer(string engine) => engine.Equals("mssql", StringComparison.OrdinalIgnoreCase) || engine.Equals("sqlserver", StringComparison.OrdinalIgnoreCase);
    private sealed class ExportBudget { public long TotalBytes; }
    private static PortableMigrationIssue Issue(string id, string code) => new() { ResourceId = id, Code = code };
    private static AppException Error(string code) => new(code, 409, 409);
    internal static string Id(string kind, string value) => kind + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..24].ToLowerInvariant();
    private static List<T> Select<T>(List<T> inventory, bool all, string[] ids, Func<T, string> key)
    {
        if (all) return inventory.ToList();
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var selected = inventory.Where(item => wanted.Contains(key(item))).ToList();
        if (wanted.Count != selected.Count) throw Error("migration.export.selection_changed");
        return selected;
    }
}
