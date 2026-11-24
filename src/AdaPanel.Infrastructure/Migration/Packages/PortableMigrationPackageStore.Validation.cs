using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Infrastructure.Migration.Packages;

public sealed partial class PortableMigrationPackageStore
{
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\\') || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl)) throw Error("path_invalid");
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || segment.Length > 255) throw Error("path_invalid");
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw Error("reserved_path");
        }
    }

    private void ValidateManifest(PortableMigrationManifest m)
    {
        if (m.Version != 1 || m.PackageId == Guid.Empty || !Enum.IsDefined(m.Provider) || m.CreatedAtUtc == default || m.CreatedAtUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(m.SourceName)) throw Error("manifest_header_invalid");
        if (m.Sites is null || m.Databases is null || m.MailDomains is null || m.DnsZones is null || m.Tasks is null || m.Payloads is null || m.Issues is null) throw Error("manifest_collection_null");
        long items = (long)m.Sites.Count + m.Databases.Count + m.MailDomains.Count + m.DnsZones.Count + m.Tasks.Count + m.Payloads.Count + m.Issues.Count;
        if (items > limits.MaxItems) throw Error("item_limit");
        if (!m.Complete && m.Issues.Count == 0) throw Error("incomplete_without_issues");
        if (m.Issues.Any(i => i is null || string.IsNullOrWhiteSpace(i.Code) || string.IsNullOrWhiteSpace(i.Detail))) throw Error("issue_invalid");
        var payloads = new Dictionary<string, PortableMigrationPayload>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var p in m.Payloads)
        {
            if (p is null) throw Error("payload_null");
            ValidatePath(p.Path);
            if (p.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || !payloads.TryAdd(p.Path, p)) throw Error("duplicate_payload");
            if (p.Size < 0 || p.Size > limits.MaxPayloadBytes || p.Size > limits.MaxTotalUncompressedBytes - size) throw Error("payload_size_limit");
            size += p.Size;
            if (p.Sha256 is null || !Regex.IsMatch(p.Sha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)) throw Error("payload_hash_invalid");
        }
        var prefixCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var path in payloads.Keys)
        {
            prefixCounts[path] = prefixCounts.GetValueOrDefault(path) + 1;
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                var prefix = path[..slash];
                prefixCounts[prefix] = prefixCounts.GetValueOrDefault(prefix) + 1;
            }
        }
        var directoryNames = new HashSet<string>(prefixCounts.Keys, StringComparer.OrdinalIgnoreCase);
        // Files cannot also be ancestors of other files, including case-only variants.
        foreach (var path in payloads.Keys)
        {
            var slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                if (payloads.ContainsKey(path[..slash])) throw Error("payload_path_conflict");
                slash = path.LastIndexOf('/', slash - 1);
            }
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Resource(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !ids.Add(id)) throw Error("resource_id_invalid");
        }
        void AddItems(int count) { items += count; if (items > limits.MaxItems) throw Error("item_limit"); }
        void References(List<string> refs)
        {
            if (refs is null || refs.Any(id => !sites.Contains(id)) || refs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != refs.Count) throw Error("site_reference_invalid");
            AddItems(refs.Count);
        }
        void Payload(string path)
        {
            ValidatePath(path);
            if (!payloads.TryGetValue(path, out var p) || p.Path != path) throw Error("payload_reference_missing");
        }
        foreach (var site in m.Sites)
        {
            if (site is null) throw Error("site_null");
            Resource(site.Id, site.Name);
            sites.Add(site.Id);
            ValidatePath(site.ContentPath);
            if (site.HostNames is null || site.EmptyDirectories is null || site.FileCount < 0) throw Error("site_invalid");
            AddItems(site.HostNames.Count + site.EmptyDirectories.Count);
            if (!string.IsNullOrEmpty(site.DocumentRootRelativePath)) ValidatePath(site.DocumentRootRelativePath);
            var prefix = site.ContentPath + "/";
            var count = prefixCounts.GetValueOrDefault(site.ContentPath);
            if (count == 0 && site.FileCount != 0 || site.FileCount is not null && site.FileCount.Value != count) throw Error("site_payload_count_mismatch");
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in site.EmptyDirectories)
            {
                ValidatePath(directory);
                if (!directories.Add(directory)) throw Error("duplicate_empty_directory");
                var full = prefix + directory;
                if (payloads.ContainsKey(full) || directoryNames.Contains(full)) throw Error("empty_directory_conflict");
            }
        }
        foreach (var db in m.Databases)
        {
            if (db is null) throw Error("database_null");
            Resource(db.Id, db.Name); References(db.SiteIds); Payload(db.DumpPath);
            if (string.IsNullOrWhiteSpace(db.Engine) || string.IsNullOrWhiteSpace(db.Format)) throw Error("database_format_missing");
        }
        foreach (var domain in m.MailDomains)
        {
            if (domain is null || domain.Accounts is null || domain.Aliases is null || domain.Lists is null) throw Error("mail_domain_null");
            Resource(domain.Id, domain.Name); References(domain.SiteIds);
            AddItems(domain.Accounts.Count + domain.Aliases.Count + domain.Lists.Count);
            var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in domain.Accounts)
            {
                if (account is null || string.IsNullOrWhiteSpace(account.Email) || !addresses.Add(account.Email) || account.QuotaMb < 0 || account.EmailCount < 0) throw Error("mail_account_invalid");
                if (!string.IsNullOrEmpty(account.ArchivePath))
                {
                    Payload(account.ArchivePath);
                    if (string.IsNullOrWhiteSpace(account.ArchiveFormat)) throw Error("mail_format_missing");
                }
                else if (account.EmailCount != 0 && (m.Complete || !m.Issues.Any(i => i.ResourceId == account.Email || i.ResourceId == domain.Id))) throw Error("mail_archive_missing");
            }
            foreach (var alias in domain.Aliases)
                if (alias is null || string.IsNullOrWhiteSpace(alias.Address) || string.IsNullOrWhiteSpace(alias.Destination)) throw Error("mail_alias_invalid");
            foreach (var list in domain.Lists)
            {
                if (list is null || string.IsNullOrWhiteSpace(list.Address) || list.Recipients is null || list.Aliases is null || list.AllowedSenders is null) throw Error("mail_list_invalid");
                AddItems(list.Recipients.Count + list.Aliases.Count + list.AllowedSenders.Count);
            }
        }
        foreach (var zone in m.DnsZones)
        {
            if (zone is null || zone.Records is null) throw Error("dns_zone_null");
            Resource(zone.Id, zone.Name); References(zone.SiteIds); AddItems(zone.Records.Count);
            if (zone.Records.Any(r => r is null || string.IsNullOrWhiteSpace(r.Type) || r.Name is null || r.Value is null || r.Ttl < 0 || r.Priority < 0)) throw Error("dns_record_invalid");
        }
        foreach (var task in m.Tasks)
        {
            if (task is null || task.ArtifactPaths is null) throw Error("task_null");
            Resource(task.Id, task.Name); References(task.SiteIds); AddItems(task.ArtifactPaths.Count);
            foreach (var path in task.ArtifactPaths) Payload(path);
            if (task.Interval < 1 || task.TimeLimitHours < 0 || string.IsNullOrWhiteSpace(task.Kind) || string.IsNullOrWhiteSpace(task.SourcePlatform)) throw Error("task_invalid");
            if (task.ScheduleUnknown && m.Complete) throw Error("unknown_schedule_marked_complete");
        }
    }
}
