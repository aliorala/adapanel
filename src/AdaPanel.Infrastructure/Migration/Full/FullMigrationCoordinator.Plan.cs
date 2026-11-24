using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed partial class FullMigrationCoordinator
{
    private static void ValidateSelection(MigrationPlanRequest request)
    {
        if (request.PackageIds.Length is 0 or > 1000 || request.PackageIds.Distinct().Count() != request.PackageIds.Length
            || request.PackageIds.Any(id => id == Guid.Empty) || request.SiteIds.Count > 10000 || request.DatabaseIds.Count > 10000
            || request.TaskIds.Count > 10000 || request.Mappings.Count > 20000 || request.AddressMappings.Count > 1000
            || request.Mappings.GroupBy(item => item.ResourceId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw Error("migration.selection_invalid");
        if (!Path.IsPathFullyQualified(request.TargetBasePath)) throw Error("migration.target_path_invalid");
    }

    private static void Assemble(MigrationPlanDto plan, Dictionary<Guid, PortableMigrationManifest> packages)
    {
        var selection = plan.Selection;
        var allSiteKeys = packages.SelectMany(pair => pair.Value.Sites.Select(site => MigrationResourceKey.Create(pair.Key, site.Id))).ToHashSet(StringComparer.Ordinal);
        var allDbKeys = packages.SelectMany(pair => pair.Value.Databases.Select(db => MigrationResourceKey.Create(pair.Key, db.Id))).ToHashSet(StringComparer.Ordinal);
        var allTaskKeys = packages.SelectMany(pair => pair.Value.Tasks.Select(task => MigrationResourceKey.Create(pair.Key, task.Id))).ToHashSet(StringComparer.Ordinal);
        if (selection.SiteIds.Any(id => !allSiteKeys.Contains(id)) || selection.DatabaseIds.Any(id => !allDbKeys.Contains(id))
            || selection.TaskIds.Any(id => !allTaskKeys.Contains(id))) throw Error("migration.selection_changed");
        var selectedSites = selection.AllSites ? allSiteKeys : selection.SiteIds.ToHashSet(StringComparer.Ordinal);
        var selectedDbs = selection.AllDatabases ? allDbKeys : selection.DatabaseIds.ToHashSet(StringComparer.Ordinal);
        foreach (var (packageId, manifest) in packages)
        {
            string Key(string id) => MigrationResourceKey.Create(packageId, id);
            var localSites = manifest.Sites.Where(site => selectedSites.Contains(Key(site.Id))).Select(site => site.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var site in manifest.Sites.Where(site => localSites.Contains(site.Id)))
                Add("site", site.Id, site.Name, [], site.RuntimeProfile);
            foreach (var db in manifest.Databases.Where(db => selectedDbs.Contains(Key(db.Id))))
                Add("database", db.Id, db.Name, db.SiteIds.Where(localSites.Contains));
            if (selection.IncludeMail)
                foreach (var mail in manifest.MailDomains.Where(mail => mail.SiteIds.Any(localSites.Contains)))
                    Add("mail", mail.Id, mail.Name, mail.SiteIds.Where(localSites.Contains));
            if (selection.IncludeDns)
                foreach (var dns in manifest.DnsZones.Where(dns => dns.SiteIds.Any(localSites.Contains)))
                    Add("dns", dns.Id, dns.Name, dns.SiteIds.Where(localSites.Contains));
            if (selection.IncludeTasks)
                foreach (var task in manifest.Tasks.Where(task => selection.TaskIds.Count == 0 || selection.TaskIds.Contains(Key(task.Id), StringComparer.Ordinal)))
                {
                    var item = Add("task", task.Id, task.Name, task.SiteIds.Where(localSites.Contains));
                    if (task.SiteIds.Any(id => !localSites.Contains(id))) item.Blockers.Add("migration.task_site_not_selected");
                    if (task.ScheduleUnknown && !item.Target.TaskMappingConfirmed) item.Blockers.Add("migration.task_mapping_required");
                    item.Warnings.Add("migration.tasks_created_disabled");
                }
            foreach (var issue in manifest.Issues)
            {
                if (!selection.IncludeTasks && (issue.Code.Contains(".task.", StringComparison.Ordinal) || issue.Code.Contains(".tasks.", StringComparison.Ordinal))) continue;
                if (!selection.IncludeMail && issue.Code.Contains(".mail.", StringComparison.Ordinal)) continue;
                if (!selection.IncludeDns && issue.Code.Contains(".dns.", StringComparison.Ordinal)) continue;
                if (!manifest.Databases.Any(database => selectedDbs.Contains(Key(database.Id))) && !selection.AllDatabases
                    && issue.Code.Contains(".database.", StringComparison.Ordinal)) continue;
                var item = plan.Items.SingleOrDefault(item => item.PackageId == packageId && item.SourceId == issue.ResourceId);
                var mailOwner = manifest.MailDomains.SingleOrDefault(domain => domain.Accounts.Any(account =>
                    account.Email.Equals(issue.ResourceId, StringComparison.OrdinalIgnoreCase)));
                if (item is null && mailOwner is not null)
                    item = plan.Items.SingleOrDefault(candidate => candidate.PackageId == packageId && candidate.Kind == "mail" && candidate.SourceId == mailOwner.Id);
                if (item is not null)
                {
                    if (IssueResolved(issue.Code, item, selection)) item.Warnings.Add(issue.Code);
                    else item.Blockers.Add(issue.Code);
                    continue;
                }
                // A known, deliberately unselected resource is excluded. Unknown metadata must
                // not disappear merely because a producer used an email address as its key.
                if (mailOwner is not null || manifest.Sites.Any(value => value.Id == issue.ResourceId)
                    || manifest.Databases.Any(value => value.Id == issue.ResourceId) || manifest.MailDomains.Any(value => value.Id == issue.ResourceId)
                    || manifest.DnsZones.Any(value => value.Id == issue.ResourceId) || manifest.Tasks.Any(value => value.Id == issue.ResourceId)) continue;
                var category = issue.ResourceId.ToLowerInvariant();
                var relevant = category is "source" or "" ||
                    category.StartsWith("site", StringComparison.Ordinal) && localSites.Count > 0 ||
                    category.StartsWith("database", StringComparison.Ordinal) && (selection.AllDatabases || selectedDbs.Any(id => id.StartsWith(packageId.ToString("N") + ":", StringComparison.Ordinal))) ||
                    category.StartsWith("db:", StringComparison.Ordinal) && selection.AllDatabases ||
                    category.StartsWith("mail", StringComparison.Ordinal) && selection.IncludeMail && localSites.Count > 0 ||
                    category.StartsWith("dns", StringComparison.Ordinal) && selection.IncludeDns && localSites.Count > 0 ||
                    category.StartsWith("task", StringComparison.Ordinal) && selection.IncludeTasks ||
                    category.StartsWith("cron", StringComparison.Ordinal) && selection.IncludeTasks ||
                    issue.Code.Contains(".mail.", StringComparison.Ordinal) && selection.IncludeMail && localSites.Count > 0 ||
                    !category.StartsWith("site", StringComparison.Ordinal) && !category.StartsWith("database", StringComparison.Ordinal)
                    && !category.StartsWith("db:", StringComparison.Ordinal) && !category.StartsWith("mail", StringComparison.Ordinal)
                    && !category.StartsWith("dns", StringComparison.Ordinal) && !category.StartsWith("task", StringComparison.Ordinal)
                    && !category.StartsWith("cron", StringComparison.Ordinal) && !issue.Code.Contains(".mail.", StringComparison.Ordinal);
                if (relevant) plan.Blockers.Add(issue.Code);
            }

            MigrationPlanItemDto Add(string kind, string sourceId, string name, IEnumerable<string> siteIds, string runtime = "auto")
            {
                var key = Key(sourceId);
                var mapping = selection.Mappings.SingleOrDefault(mapping => mapping.ResourceId == key) ?? new()
                { ResourceId = key, TargetName = name, RuntimeProfile = string.IsNullOrWhiteSpace(runtime) ? "auto" : runtime };
                if (string.IsNullOrWhiteSpace(mapping.TargetName)) mapping.TargetName = name;
                if (kind == "site" && string.IsNullOrWhiteSpace(mapping.TargetPath))
                    mapping.TargetPath = Path.Combine(selection.TargetBasePath, mapping.TargetName);
                if (kind == "database" && string.IsNullOrWhiteSpace(mapping.DatabaseUserName))
                {
                    var safe = new string(mapping.TargetName.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_').ToArray());
                    mapping.DatabaseUserName = (safe.Length > 20 ? safe[..20] : safe) + "_mig";
                }
                var item = new MigrationPlanItemDto
                {
                    Id = key, PackageId = packageId, Kind = kind, SourceId = sourceId, SourceName = name,
                    Target = mapping, Dependencies = siteIds.Select(Key).Distinct(StringComparer.Ordinal).ToList()
                };
                if ((kind is "database" or "mail") && !selection.AcceptNewCredentials)
                    item.Blockers.Add("migration.new_credentials_confirmation_required");
                if (kind is "mail" or "dns")
                {
                    // Renaming mail/DNS domains also changes addresses, aliases and records; require
                    // an explicit supported conversion rather than partially renaming the envelope.
                    if (!name.Equals(mapping.TargetName, StringComparison.OrdinalIgnoreCase)) item.Blockers.Add("migration.domain_rename_unsupported");
                }
                plan.Items.Add(item);
                return item;
            }
        }
        // Site data first, then independent DBs, mail and DNS; imported tasks remain disabled.
        var order = new Dictionary<string, int> { ["site"] = 0, ["database"] = 1, ["mail"] = 2, ["dns"] = 3, ["task"] = 4 };
        plan.Items = plan.Items.OrderBy(item => order[item.Kind]).ToList();
        if (plan.Items.Count == 0) plan.Blockers.Add("migration.selection_required");
        if (selection.Mappings.Any(mapping => !plan.Items.Any(item => item.Id == mapping.ResourceId)))
            plan.Blockers.Add("migration.mapping_not_selected");
    }

    private static bool IssueResolved(string code, MigrationPlanItemDto item, MigrationPlanRequest selection) => code switch
    {
        "cpanel.database.credentials_required" or "cpanel.mail.credentials_required" or "cpanel.mail.new_password_required"
            or "plesk.mail.new_password_required" => selection.AcceptNewCredentials,
        "cpanel.mail.quota_unknown" or "plesk.mail.quota_unknown" => item.Target.UnknownMailboxQuotaMb is >= 0,
        "cpanel.mail.active_state_unknown" or "plesk.mail.active_state_unknown" => item.Target.UnknownMailboxActive.HasValue,
        "cpanel.database.site_mapping_required" or "migration.export.database_site_unknown" => true, // DBs may intentionally be independent.
        "cpanel.site.runtime_unknown" or "plesk.site.runtime_selection_required" => item.Target.RuntimeProfile != "auto" && item.Target.RuntimeProfile.Length > 0,
        "cpanel.task.schedule_unknown" or "cpanel.task.command_unsupported" or "cpanel.task.site_mapping_required"
            or "plesk.task.command_mapping_required" => item.Target.TaskMappingConfirmed,
        _ => false
    };
}
