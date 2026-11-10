using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;
using static AdaPanel.Infrastructure.Migration.Sources.PleskBackupXml;

namespace AdaPanel.Infrastructure.Migration.Sources;

/// <summary>Metadata-driven parser for Plesk panel full exports. Never guesses payload ownership from filenames.</summary>
public sealed class PleskBackupNormalizer(IOptions<PanelRuntimeOptions> runtime,
    IOptions<PortableMigrationPackageOptions> options, IMigrationPackageStore packages, IIisSiteService? sites = null)
{
    public Task<PortableMigrationPackage> NormalizeAsync(Stream input, string format, CancellationToken cancellationToken = default)
        => NormalizeCoreAsync(input, format, false, null, cancellationToken);

    /// <summary>Only a server-side, successful full native export receipt may select this entry point.</summary>
    internal Task<PortableMigrationPackage> NormalizeFullExportAsync(Stream input, string format, CancellationToken cancellationToken = default)
        => NormalizeCoreAsync(input, format, true, null, cancellationToken);

    internal Task<PortableMigrationPackage> NormalizeFullExportAsync(Stream input, string format, string expectedSubscription, CancellationToken cancellationToken = default)
        => NormalizeCoreAsync(input, format, true, expectedSubscription, cancellationToken);

    private async Task<PortableMigrationPackage> NormalizeCoreAsync(Stream input, string format, bool trustedFullReceipt, string? expectedSubscription, CancellationToken token)
    {
        format = format.ToLowerInvariant() switch
        {
            "tar" or "plesk-backup-tar" => "tar", "zip" or "plesk-backup-zip" => "zip",
            "tgz" or "tar.gz" => "tgz", "tzst" or "tar.zst" => "tzst", _ => throw Error("archive_format_unsupported")
        };
        var publicRoots = await MigrationStoragePolicy.PublicRootsAsync(sites, options.Value.PublicWebRoots, token);
        var parent = Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(runtime.Value.DataRoot), "migration-normalization"));
        PortableMigrationStorage.CreatePrivateDirectory(parent, publicRoots);
        var stage = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        PortableMigrationStorage.CreatePrivateDirectory(stage, publicRoots);
        try
        {
            var inputPath = Path.Combine(stage, "source." + format);
            await using (var saved = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await SourceArchiveReader.CopyAsync(input, saved, options.Value.MaxUploadBytes, token);
            var archives = new PleskArchiveReader(stage, options.Value, token);
            var inventory = await archives.ExtractAsync(inputPath, format);
            if (inventory.Files.Keys.Any(path => Regex.IsMatch(path, @"_[0-9]{10,14}_[0-9]{10,14}(?:\.|_)", RegexOptions.CultureInvariant) || Regex.IsMatch(path, @"\.(?:tar|tgz|tzst|zip)\.[0-9]+$", RegexOptions.CultureInvariant)))
                throw Error("incremental_or_multivolume_unsupported");
            var infos = inventory.Files.Keys.Where(path => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && path.Split('/').Last().Contains("_info_", StringComparison.Ordinal)).OrderBy(path => path.Count(c => c == '/')).ThenBy(path => path, StringComparer.Ordinal).ToArray();
            if (infos.Length == 0 || infos.Length > options.Value.MaxItems) throw Error("metadata_missing");
            var depth = infos[0].Count(c => c == '/');
            if (infos.Count(path => path.Count(c => c == '/') == depth) != 1) throw Error("metadata_ambiguous");
            var metadataPath = infos[0];
            var xml = await ReadAsync(inventory.Files[metadataPath], options.Value.MaxManifestBytes, token);
            var context = new Context(inventory, metadataPath, xml, archives, stage, options.Value, trustedFullReceipt, expectedSubscription, token);
            await context.BuildAsync();
            return await PortablePackageWriter.UploadAsync(context.Manifest(), context.Payloads, packages, stage, token);
        }
        finally { if (Directory.Exists(stage)) PortableMigrationStorage.DeleteOwnedDirectory(stage); }
    }

    private sealed class Context(SourceArchiveInventory source, string metadataPath, XElement xml, PleskArchiveReader archives,
        string stage, PortableMigrationPackageOptions limits, bool fullReceipt, string? expectedSubscription, CancellationToken token)
    {
        internal readonly List<(string Path, string SourcePath)> Payloads = [];
        private readonly List<PortableMigrationPayload> descriptions = [];
        private readonly List<PortableMigrationIssue> issues = [];
        private readonly List<PortableMigrationSite> sites = [];
        private readonly List<PortableMigrationDatabase> databases = [];
        private readonly List<PortableMigrationMailDomain> mail = [];
        private readonly List<PortableMigrationDnsZone> dns = [];
        private readonly List<PortableMigrationTask> tasks = [];
        private readonly HashSet<string> resourceIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<XElement> consumed = [];
        private long normalizedBytes;
        private string platform = "";
        private string metadataBase = metadataPath.Contains('/') ? metadataPath[..metadataPath.LastIndexOf('/')] : "";
        internal PortableMigrationManifest Manifest() => new()
        {
            PackageId = Guid.NewGuid(), Provider = PortableMigrationProvider.Plesk, CreatedAtUtc = DateTimeOffset.UtcNow,
            SourceName = expectedSubscription ?? "Plesk " + Regex.Match(Attribute(xml, "dump-version"), @"\A\d{1,4}(?:\.\d{1,4}){1,4}\z", RegexOptions.CultureInvariant).Value, Complete = issues.Count == 0, Issues = issues,
            Sites = sites, Databases = databases, MailDomains = mail, DnsZones = dns, Tasks = tasks, Payloads = descriptions
        };
        internal async Task BuildAsync()
        {
            var dumpInfo = Child(xml, "dump-info");
            platform = Attribute(Child(dumpInfo, "os-description"), "type") switch
            { "unix" or "linux" => "linux", "windows" or "win" => "windows", _ => "" };
            if (platform.Length == 0) throw Error("source_platform_unknown");
            if (!fullReceipt) Issue("plesk.full_backup_unverified", "source", "A successful server-side native full-export receipt is required; content-included alone does not prove a full backup.");
            var domains = xml.Descendants().Where(element => element.Name.LocalName == "domain" && (element.Parent?.Name.LocalName is "domains" or "migration-dump") && !element.Ancestors().Any(parent => parent.Name.LocalName == "domain")).ToArray();
            if (domains.Length == 0) throw Error("domain_inventory_unsupported");
            if (expectedSubscription is not null && (domains.Length != 1 || Host(Value(domains[0], "name")) != Host(expectedSubscription)))
                throw Error("subscription_scope_mismatch");
            foreach (var domain in domains)
            {
                token.ThrowIfCancellationRequested();
                var host = Host(Value(domain, "name"));
                await Site(domain, host);
                foreach (var child in domain.Descendants().Where(element => element.Name.LocalName is "site" or "subdomain" && element.Parent?.Name.LocalName is "sites" or "subdomains"))
                {
                    var name = Value(child, "name");
                    await Site(child, Host(name.Contains('.') ? name : name + "." + host));
                }
                await Databases(domain, host);
                await Mail(domain, host);
                Dns(domain, host);
                await Tasks(domain, host);
            }
            if (xml.Descendants().Any(element => element.Name.LocalName == "domain" && !domains.Contains(element)))
                Issue("plesk.domain.shape_unsupported", "sites", "A domain descriptor outside the supported hierarchy requires explicit mapping.");
            foreach (var cid in xml.Descendants().Where(element => element.Name.LocalName == "cid"))
            {
                if (consumed.Contains(cid)) continue;
                if (Attribute(cid, "type") is "logs" or "statistics" or "apache-files" or "conf" or "pd" or "certificate" or "ssl" or "error-docs") continue;
                Issue("plesk.content_reference_unsupported", "source", "A declared content component was not mapped; destination apply requires review.");
            }
            if (sites.Count + databases.Count + mail.Count + dns.Count == 0) throw Error("required_objects_unavailable");
            if (archives.HasZipWallClockDates) Issue("plesk.zip_source_timezone_unverified", "source", "ZIP wall-clock timestamps do not prove the source UTC timezone; timestamp mapping requires review.");
        }
        private async Task Site(XElement element, string host)
        {
            var hosting = Child(element, "phosting");
            if (hosting == null) { Issue("plesk.site.hosting_unsupported", "site:" + host, "The declared domain does not have supported physical hosting metadata."); return; }
            var id = "site:" + host;
            Unique(id);
            var docroot = Value(hosting, "www-root");
            var cids = Children(Child(hosting, "content"), "cid").ToArray();
            var cid = cids.SingleOrDefault(item => Attribute(item, "type") == "docroot")
                ?? cids.SingleOrDefault(item => Attribute(item, "type") == "user-data");
            if (cid == null || docroot.Length == 0) { Issue("plesk.site.metadata_missing", id, "An explicit document root and content reference are required."); return; }
            var resolved = await Resolve(cid, id);
            if (resolved == null) return;
            var (inventory, root) = resolved.Value;
            if (Attribute(cid, "type") == "user-data")
            {
                var relative = docroot.Replace('\\', '/').TrimEnd('/');
                if (relative.StartsWith('/') || Regex.IsMatch(relative, "^[A-Za-z]:"))
                {
                    var home = Value(Child(Child(hosting, "preferences"), "sysuser"), "home").Replace('\\', '/').TrimEnd('/');
                    if (home.Length == 0 || !relative.StartsWith(home + "/", platform == "windows" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    { Issue("plesk.site.docroot_mapping_required", id, "The absolute document root cannot be mapped to the referenced user-data archive."); return; }
                    relative = relative[(home.Length + 1)..];
                }
                root = Join(root, Relative(relative));
            }
            if (!RootExists(inventory, root)) { Issue("plesk.site.content_missing", id, "The declared document root is absent from its referenced archive."); return; }
            var prefix = "sites/" + (sites.Count + 1).ToString(CultureInfo.InvariantCulture);
            var rootPrefix = root.Length == 0 ? "" : root + "/";
            long count = 0;
            foreach (var item in inventory.Files.Where(item => item.Key.StartsWith(rootPrefix, StringComparison.Ordinal)))
            {
                var relative = item.Key[rootPrefix.Length..];
                if (!WindowsPath(relative)) { Issue("plesk.site.filename_unsupported", id, "A source filename cannot be safely represented by Windows hosting."); continue; }
                await Payload(prefix + "/" + relative, item.Value); count++;
            }
            var empty = new List<string>();
            foreach (var directory in inventory.Directories.Where(path => path.StartsWith(rootPrefix, StringComparison.Ordinal) && path != root && !inventory.Files.Keys.Any(file => file.StartsWith(path + "/", StringComparison.Ordinal))))
            {
                var relative = directory[rootPrefix.Length..];
                if (WindowsPath(relative)) empty.Add(relative);
                else Issue("plesk.site.filename_unsupported", id, "An empty source directory cannot be safely represented by Windows hosting.");
            }
            var php = Value(hosting, "php-version");
            var hosts = new List<string> { host };
            foreach (var alias in Children(Child(element, "domain-aliases"), "domain-alias")) hosts.Add(Host(Value(alias, "name")));
            if (Child(element, "aliases") != null) Issue("plesk.site.alias_shape_unsupported", id, "An additional alias descriptor requires explicit mapping.");
            sites.Add(new()
            {
                Id = id, Name = host, ContentPath = prefix, SourcePhysicalPath = docroot, FileCount = count,
                EmptyDirectories = empty, HostNames = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), RuntimeProfile = php.Length > 0 ? "php" : "", PhpVersion = php
            });
            if (php.Length == 0) Issue("plesk.site.runtime_selection_required", id, "The destination runtime must be selected explicitly.");
        }
        private async Task Databases(XElement domain, string host)
        {
            if (Child(domain, "databases") is { } section && section.Elements().Any(element => element.Name.LocalName != "database"))
                Issue("plesk.database.shape_unsupported", "site:" + host, "An additional database descriptor requires explicit mapping.");
            foreach (var database in Children(Child(domain, "databases"), "database"))
            {
                var name = Value(database, "name");
                if (!Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9_]{1,127}\z", RegexOptions.CultureInvariant)) throw Error("database_name_invalid");
                var engine = Value(database, "type").ToLowerInvariant() switch
                { "mysql" => "mysql", "mariadb" => "mariadb", "postgresql" or "postgres" => "postgresql", "mssql" or "sqlserver" => "mssql", _ => "" };
                var id = "database:" + host + ":" + engine + ":" + name;
                Unique(id);
                if (engine.Length == 0) { Issue("plesk.database.engine_unsupported", id, "The declared database engine was not recognized."); continue; }
                var cid = Children(Child(database, "content"), "cid").SingleOrDefault(item => Attribute(item, "type") is "sqldump" or "database");
                if (cid == null) { Issue("plesk.database.dump_missing", id, "A database was declared without an explicit SQL/native dump reference."); continue; }
                var resolved = await Resolve(cid, id);
                if (resolved == null) continue;
                var (inventory, offset) = resolved.Value;
                var candidates = inventory.Files.Where(item => item.Key == offset || item.Key.StartsWith(offset.Length == 0 ? "" : offset + "/", StringComparison.Ordinal)).ToArray();
                if (candidates.Length != 1 || new FileInfo(candidates[0].Value).Length == 0)
                { Issue("plesk.database.dump_ambiguous_or_empty", id, "The referenced database payload is absent, empty or not unambiguous."); continue; }
                var dump = "databases/" + (databases.Count + 1) + (engine == "mssql" ? ".bak" : ".sql");
                await Payload(dump, candidates[0].Value);
                databases.Add(new() { Id = id, Name = name, Engine = engine, DumpPath = dump, Format = engine == "mssql" ? "mssql-backup" : "sql", SiteIds = sites.Where(site => site.Name == host).Select(site => site.Id).ToList() });
            }
        }
        private async Task Mail(XElement domain, string host)
        {
            var system = Child(domain, "mailsystem");
            if (system == null) return;
            var id = "mail:" + host; Unique(id);
            var accounts = new List<PortableMigrationMailAccount>();
            var aliases = new List<PortableMigrationMailAlias>();
            if (Child(system, "mailusers") == null) Issue("plesk.mail.inventory_missing", id, "The declared mail system has no readable mailbox inventory.");
            if (Child(system, "mailusers") is { } users && users.Elements().Any(element => element.Name.LocalName != "mailuser"))
                Issue("plesk.mail.account_shape_unsupported", id, "Additional mail user descriptors require explicit mapping.");
            if (system.Descendants().Any(element => element.Name.LocalName is "mailing-list" or "mailman"))
                Issue("plesk.mail.list_unsupported", id, "Mailing-list configuration requires an explicit destination adapter.");
            foreach (var user in Children(Child(system, "mailusers"), "mailuser"))
            {
                var name = Value(user, "name");
                if (!Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._+%-]{0,63}\z", RegexOptions.CultureInvariant)) throw Error("mail_account_invalid");
                var email = name + "@" + host;
                var preferences = Child(user, "preferences");
                foreach (var redirect in Children(preferences, "redirect").Concat(Children(preferences, "mailgroup-member")))
                {
                    var destination = redirect.Value.Trim();
                    if (!destination.Contains('@')) destination += "@" + host;
                    if (!Regex.IsMatch(destination, @"\A[A-Za-z0-9][A-Za-z0-9._+%-]{0,63}@[A-Za-z0-9.-]+\z", RegexOptions.CultureInvariant))
                    { Issue("plesk.mail.alias_invalid", id, "A forwarding destination requires explicit mapping."); continue; }
                    aliases.Add(new() { Address = email, Destination = destination });
                }
                var mailbox = Child(Child(user, "preferences"), "mailbox");
                var cid = Children(Child(mailbox, "content"), "cid").SingleOrDefault(item => Attribute(item, "type") == "mailbox");
                if (mailbox == null) { Issue("plesk.mail.account_shape_unsupported", id, "A declared mail user has no supported mailbox descriptor."); continue; }
                Issue("plesk.mail.quota_unknown", email, "The backup mailbox quota field/unit is not verified; select the target quota explicitly.");
                Issue("plesk.mail.active_state_unknown", email, "The backup does not prove the effective mailbox login/active state; select the target state explicitly.");
                Issue("plesk.mail.new_password_required", email, "A new target mailbox password is required; source passwords and hashes were not normalized.");
                await PreserveMailboxSettings(user, email);
                if (cid == null) { Issue("plesk.mail.content_missing", id, "A declared mailbox has no explicit content reference."); accounts.Add(new() { Email = email }); continue; }
                var resolved = await Resolve(cid, id);
                if (resolved == null) { accounts.Add(new() { Email = email }); continue; }
                var (inventory, root) = resolved.Value;
                if (!inventory.Directories.Contains(Join(root, "cur")) || !inventory.Directories.Contains(Join(root, "new")) || !inventory.Directories.Contains(Join(root, "tmp")))
                {
                    Issue(platform == "windows" ? "plesk.mail.mailenable_conversion_unsupported" : "plesk.mail.maildir_missing", id,
                        "The referenced payload is not a proven Maildir. MailEnable messages require a separate conversion adapter.");
                    accounts.Add(new() { Email = email }); continue;
                }
                var local = Path.Combine(stage, "mail-" + Guid.NewGuid().ToString("N") + ".zip");
                if (root.Length == 0)
                {
                    inventory = new SourceArchiveInventory(
                        inventory.Files.ToDictionary(item => "mailbox/" + item.Key, item => item.Value, StringComparer.Ordinal),
                        inventory.Directories.Select(path => "mailbox/" + path).Append("mailbox").ToHashSet(StringComparer.Ordinal))
                    { FileMetadata = inventory.FileMetadata.ToDictionary(item => "mailbox/" + item.Key, item => item.Value, StringComparer.Ordinal) };
                    root = "mailbox";
                }
                var packed = await PortableMaildirWriter.CreateAsync(inventory, root, local, limits, token);
                var payload = "mail/" + (mail.Count + 1) + "/" + (accounts.Count + 1) + ".zip";
                await Payload(payload, local);
                accounts.Add(new() { Email = email, EmailCount = packed.EmailCount, ArchiveFormat = "maildir-zip", ArchivePath = payload });
            }
            mail.Add(new() { Id = id, Name = host, Accounts = accounts, Aliases = aliases, SiteIds = sites.Where(site => site.Name == host).Select(site => site.Id).ToList() });
        }
        private async Task PreserveMailboxSettings(XElement user, string email)
        {
            var settings = new List<object>();
            void Preserve(XElement? owner, string scope, params string[] names)
            {
                if (owner is null) return;
                foreach (var name in names)
                {
                    if (owner.Attribute(name) is { } attribute)
                        settings.Add(new { scope, kind = "attribute", name, raw = (string?)attribute.Value, structured = false, qualifiers = Array.Empty<object>() });
                    foreach (var child in Children(owner, name))
                        settings.Add(new { scope, kind = "child", name, raw = child.HasElements ? null : child.Value, structured = child.HasElements,
                            qualifiers = child.Attributes().Where(a => a.Name.NamespaceName.Length == 0 && a.Name.LocalName is "unit" or "units" or "type" or "inherit" or "unlimited")
                                .Select(a => new { name = a.Name.LocalName, raw = a.Value }).ToArray() });
                }
            }
            Preserve(user, "mailuser", "enabled", "status", "quota", "mailbox-quota", "quota-unit", "quota-units");
            Preserve(Child(user, "mailbox"), "mailuser/mailbox", "enabled", "status", "quota", "quota-unit", "quota-units");
            var preferences = Child(user, "preferences");
            Preserve(preferences, "mailuser/preferences", "enabled", "status", "quota", "mailbox-quota", "quota-unit", "quota-units");
            Preserve(Child(preferences, "mailbox"), "mailuser/preferences/mailbox", "enabled", "status", "quota", "quota-unit", "quota-units");
            var local = Path.Combine(stage, "mail-settings-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllBytesAsync(local, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, email, source = "plesk-backup-xml", interpretation = "unverified-source-settings", fields = settings
            }), token);
            await Payload("source/mail-settings/" + (mail.Count + 1) + "/" + Guid.NewGuid().ToString("N") + ".json", local);
        }
        private void Dns(XElement domain, string host)
        {
            var zone = Child(Child(domain, "properties"), "dns-zone");
            if (zone == null) return;
            var id = "dns:" + host; Unique(id);
            var records = new List<PortableMigrationDnsRecord>();
            foreach (var record in Children(zone, "dnsrec"))
            {
                var type = Value(record, "type").ToUpperInvariant();
                if (type is not ("A" or "AAAA" or "NS" or "CNAME" or "MX" or "TXT" or "SRV" or "CAA" or "SOA" or "PTR" or "DS"))
                { Issue("plesk.dns.record_unsupported", id, "An unrecognized DNS record requires manual mapping."); continue; }
                var value = type == "TXT" ? RawValue(record, "dst") : Value(record, "dst");
                var name = Value(record, "src");
                if (value is null || type != "TXT" && (value.Length == 0 || value.Any(char.IsControl)) || name.Any(char.IsControl)) { Issue("plesk.dns.record_invalid", id, "A declared DNS record is incomplete."); continue; }
                var priority = type == "MX" ? Number(record, "opt") : 0;
                var ttl = Number(record, "ttl", Number(zone, "ttl"));
                if (priority > int.MaxValue || ttl > int.MaxValue) throw Error("dns_number_invalid");
                records.Add(new() { Type = type, Name = name, Value = value, Priority = (int)priority, Ttl = (int)ttl });
            }
            if (zone.Elements().Any(element => element.Name.LocalName is not ("dnsrec" or "ttl")))
                Issue("plesk.dns.shape_unsupported", id, "Additional DNS metadata requires review.");
            dns.Add(new() { Id = id, Name = host, Records = records, SiteIds = sites.Where(site => site.Name == host).Select(site => site.Id).ToList() });
        }
        private async Task Tasks(XElement domain, string host)
        {
            if (Child(domain, "scheduled-tasks") is { } section && section.Elements().Any(element => element.Name.LocalName != "task"))
                Issue("plesk.task.shape_unsupported", "site:" + host, "A scheduled task descriptor requires explicit mapping.");
            foreach (var task in Children(Child(domain, "scheduled-tasks"), "task"))
            {
                var id = "task:" + host + ":" + (tasks.Count + 1);
                Unique(id);
                var raw = Path.Combine(stage, "task-" + Guid.NewGuid().ToString("N"));
                // Store only task command/schedule, never the surrounding user/password/private-key XML.
                var text = Value(task, "schedule") + "\n" + Value(task, "command");
                await File.WriteAllTextAsync(raw, text, new UTF8Encoding(false), token);
                var payload = "tasks/" + (tasks.Count + 1) + ".txt";
                await Payload(payload, raw);
                tasks.Add(new() { Id = id, Name = "Plesk task " + (tasks.Count + 1), Kind = "shell", SourcePlatform = platform,
                    ScheduleUnknown = true, ScheduleType = "unknown", ArtifactPaths = [payload], SiteIds = sites.Where(site => site.Name == host).Select(site => site.Id).ToList() });
                Issue("plesk.task.command_mapping_required", id, "A source task requires explicit command/schedule mapping before any Windows job is created.");
            }
            if (domain.Descendants().Any(element => element.Name.LocalName is "crontab" or "cron"))
                Issue("plesk.task.cron_shape_unsupported", "site:" + host, "The declared cron descriptor requires manual mapping.");
        }
        private async Task<(SourceArchiveInventory Inventory, string Root)?> Resolve(XElement cid, string resource)
        {
            consumed.Add(cid);
            var refs = Children(cid, "content-file").ToArray();
            if (refs.Length > 1) throw Error("incremental_or_multivolume_unsupported");
            if (refs.Length != 1 || string.IsNullOrWhiteSpace(refs[0].Value)) { Issue("plesk.content_reference_missing", resource, "A declared component has no content-file reference."); return null; }
            var file = Relative(refs[0].Value.Trim());
            var path = Relative(Attribute(cid, "path"));
            var candidates = new[] { Join(metadataBase, path, file), Join(path, file) }.Distinct(StringComparer.Ordinal).Where(source.Files.ContainsKey).ToArray();
            if (candidates.Length == 0) { Issue("plesk.content_payload_missing", resource, "An explicitly referenced source payload is missing."); return null; }
            if (candidates.Length != 1) throw Error("content_reference_ambiguous");
            var disk = source.Files[candidates[0]];
            var offset = Relative(Attribute(cid, "offset"));
            var format = PleskArchiveReader.Format(file);
            if (format.Length == 0)
            {
                if (Attribute(cid, "type") is not ("sqldump" or "database")) { Issue("plesk.content_format_unsupported", resource, "A referenced content format cannot be extracted safely."); return null; }
                var logical = offset.Length == 0 ? "dump" : offset;
                return (new SourceArchiveInventory(new Dictionary<string, string> { [logical] = disk }, new HashSet<string>()), logical);
            }
            var inventory = await archives.ExtractAsync(disk, format, 1);
            var nestedDepth = 1;
            // A nested archive is followed only when the XML's exact offset explicitly names it.
            while (inventory.Files.TryGetValue(offset, out var nested) && PleskArchiveReader.Format(offset) is { Length: > 0 } nestedFormat)
            {
                inventory = await archives.ExtractAsync(nested, nestedFormat, ++nestedDepth);
                offset = "";
            }
            if (!RootExists(inventory, offset) && !inventory.Files.ContainsKey(offset))
            { Issue("plesk.content_offset_missing", resource, "The declared content offset is absent from its referenced archive."); return null; }
            return (inventory, offset);
        }
        private async Task Payload(string path, string disk)
        {
            var description = await PortablePackageWriter.DescribeAsync(path, disk, token);
            if (description.Size > limits.MaxPayloadBytes || description.Size > limits.MaxTotalUncompressedBytes - normalizedBytes || descriptions.Count >= limits.MaxItems) throw Error("normalized_payload_limit");
            normalizedBytes += description.Size; descriptions.Add(description); Payloads.Add((path, disk));
        }
        private void Unique(string id) { if (!resourceIds.Add(id) || resourceIds.Count > limits.MaxItems) throw Error("duplicate_resource_or_limit"); }
        private void Issue(string code, string resource, string detail)
        {
            if (issues.Any(issue => issue.Code == code && issue.ResourceId == resource)) return;
            if (issues.Count >= limits.MaxItems) throw Error("issue_limit");
            issues.Add(new() { Code = code, ResourceId = resource, Detail = detail });
        }
        private static string Host(string value)
        {
            value = value.Trim().TrimEnd('.').ToLowerInvariant();
            if (value.Length is 0 or > 253 || !value.Contains('.') || value.Split('.').Any(part => part.Length is 0 or > 63 || !Regex.IsMatch(part, @"\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\z", RegexOptions.CultureInvariant))) throw Error("domain_invalid");
            return value;
        }
        private static string Relative(string path)
        {
            path = path.Replace('\\', '/');
            if (path.Length == 0) return path;
            if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            path = path.TrimEnd('/');
            if (path.Length == 0) return path;
            SourceArchiveReader.ValidateLogicalPath(path); return path;
        }
        private static string Join(params string[] values) => string.Join('/', values.Where(value => value.Length > 0));
        private static bool RootExists(SourceArchiveInventory inventory, string root) => root.Length == 0 || inventory.Directories.Contains(root) || inventory.Files.Keys.Any(path => path.StartsWith(root + "/", StringComparison.Ordinal));
        private static bool WindowsPath(string path) => path.Length > 0 && path.Split('/').All(part => part.Length is > 0 and <= 255 && !part.EndsWith('.') && !part.EndsWith(' ') && part.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) < 0
            && !Regex.IsMatch(part.Split('.')[0], @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }
}
