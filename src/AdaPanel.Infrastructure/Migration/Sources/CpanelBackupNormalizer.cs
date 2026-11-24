using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AdaPanel.Infrastructure.Migration.Sources;

/// <summary>Recognizes the documented single-account cp/userdata/homedir/mysql/psql layout.</summary>
public sealed class CpanelBackupNormalizer(IOptions<PanelRuntimeOptions> runtime,
    IOptions<PortableMigrationPackageOptions> options, IMigrationPackageStore packages, IIisSiteService? sites = null) : IMigrationBackupNormalizer
{
    public Task<PortableMigrationPackage> NormalizeAsync(string provider, Stream input, CancellationToken cancellationToken = default)
        => string.Equals(provider, "cpanel", StringComparison.OrdinalIgnoreCase) ? NormalizeAsync(input, cancellationToken)
            : throw new InvalidDataException("migration.cpanel.provider_unsupported");

    public async Task<PortableMigrationPackage> NormalizeAsync(Stream input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var publicRoots = await MigrationStoragePolicy.PublicRootsAsync(sites, options.Value.PublicWebRoots, cancellationToken);
        var parent = Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(runtime.Value.DataRoot), "migration-sources"));
        PortableMigrationStorage.CreatePrivateDirectory(parent, publicRoots);
        var stage = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        PortableMigrationStorage.CreatePrivateDirectory(stage, publicRoots);
        try
        {
            var inventory = Unwrap(await SourceArchiveReader.ExtractTarGzipAsync(input, stage, options.Value, cancellationToken));
            if (!inventory.Files.ContainsKey("version") || !inventory.Directories.Contains("cp")
                || !inventory.Directories.Contains("userdata") || !inventory.Directories.Contains("homedir"))
                throw new InvalidDataException("migration.cpanel.layout_unsupported");
            var context = new Context(inventory, stage, options.Value, cancellationToken);
            await context.BuildAsync();
            return await PortablePackageWriter.UploadAsync(context.Manifest(), context.Payloads, packages, stage, cancellationToken);
        }
        finally { if (Directory.Exists(stage)) PortableMigrationStorage.DeleteOwnedDirectory(stage); }
    }

    private static SourceArchiveInventory Unwrap(SourceArchiveInventory inventory)
    {
        if (inventory.Files.ContainsKey("version")) return inventory;
        var tops = inventory.Files.Keys.Concat(inventory.Directories).Select(p => p.Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray();
        if (tops.Length != 1) return inventory;
        var prefix = tops[0] + "/";
        return new(inventory.Files.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(p => p.Key[prefix.Length..], p => p.Value, StringComparer.Ordinal),
            inventory.Directories.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p[prefix.Length..]).ToHashSet(StringComparer.Ordinal))
        { FileMetadata = inventory.FileMetadata.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(p => p.Key[prefix.Length..], p => p.Value, StringComparer.Ordinal) };
    }

    private sealed class Context(SourceArchiveInventory source, string stage, PortableMigrationPackageOptions limits, CancellationToken token)
    {
        internal readonly List<(string Path, string SourcePath)> Payloads = [];
        private readonly List<PortableMigrationPayload> descriptions = [];
        private readonly List<PortableMigrationIssue> issues = [];
        private readonly List<PortableMigrationSite> sites = [];
        private readonly List<PortableMigrationDatabase> databases = [];
        private readonly List<PortableMigrationMailDomain> domains = [];
        private readonly List<PortableMigrationDnsZone> zones = [];
        private readonly List<PortableMigrationTask> tasks = [];
        private readonly Dictionary<string, string> sourceToPayload = new(StringComparer.Ordinal);
        private readonly HashSet<string> payloadNames = new(StringComparer.Ordinal);
        private readonly HashSet<(string Code, string Resource, string Detail)> issueKeys = [];
        private readonly Dictionary<string, List<string>> siteByHost = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<PortableMigrationSite>> siteByRoot = new(StringComparer.Ordinal);
        private long itemCount;
        private string account = "cpanel-account";
        private long normalizedBytes;
        internal PortableMigrationManifest Manifest() => new()
        {
            PackageId = Guid.NewGuid(), Provider = PortableMigrationProvider.Cpanel, CreatedAtUtc = DateTimeOffset.UtcNow,
            SourceName = account, Complete = issues.Count == 0, Issues = issues, Sites = sites, Databases = databases,
            MailDomains = domains, DnsZones = zones, Tasks = tasks, Payloads = descriptions
        };
        internal async Task BuildAsync()
        {
            var version = (await Text("version")).Trim();
            if (string.IsNullOrWhiteSpace(version) || version.Length > 256 || version.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c)))
                Issue("cpanel.archive_version_malformed", "source", "The archive version descriptor is absent or malformed.");
            var cp = source.Files.Keys.Where(p => p.StartsWith("cp/", StringComparison.Ordinal) && p.Count(c => c == '/') == 1).ToArray();
            if (cp.Length != 1) throw new InvalidDataException("migration.cpanel.single_account_required");
            var user = cp[0][3..];
            if (!Regex.IsMatch(user, "^[a-zA-Z0-9_]{1,64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("migration.cpanel.account_invalid");
            account = user;
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Lines(await Text(cp[0])))
            {
                var equal = line.IndexOf('=');
                if (equal > 0 && Regex.IsMatch(line[..equal], "^DNS[0-9]*$", RegexOptions.CultureInvariant))
                {
                    var domain = Host(line[(equal + 1)..].Trim());
                    if (domain is not null) expected.Add(domain);
                }
            }
            await ReadMain(expected);
            await ReadSites(expected);
            await ReadDatabases();
            await ReadMail();
            await ReadDns();
            await ReadTasks();
            foreach (var path in source.Files.Keys.Where(p => p.StartsWith("mm/", StringComparison.Ordinal) || p.StartsWith("mma/", StringComparison.Ordinal) || p.StartsWith("mms/", StringComparison.Ordinal) || p.StartsWith("vf/", StringComparison.Ordinal) || p.StartsWith("vad/", StringComparison.Ordinal)))
            {
                Issue("cpanel.mail.feature_unsupported", "mail", "Mailman lists, Exim filters or virtual domain alias metadata requires manual mapping.");
                break;
            }
        }
        private async Task ReadMain(HashSet<string> expected)
        {
            if (!source.Files.ContainsKey("userdata/main")) { Issue("cpanel.domain_inventory_unverified", "sites", "The main domain inventory is missing."); return; }
            try
            {
                var yaml = CpanelYaml.Read(await Text("userdata/main"));
                var main = Host(CpanelYaml.Scalar(yaml, "main_domain"));
                if (main is not null) expected.Add(main);
                foreach (var name in CpanelYaml.Strings(yaml, "sub_domains"))
                    if (Host(name) is { } domain) expected.Add(domain); else Issue("cpanel.domain_invalid", "sites", "A subdomain name could not be normalized.");
                if (yaml.Children.TryGetValue(new YamlScalarNode("addon_domains"), out var addons))
                {
                    if (addons is not YamlMappingNode map) throw new InvalidDataException("migration.cpanel.addons_invalid");
                    foreach (var key in map.Children.Keys)
                        if (key is YamlScalarNode scalar && Host(scalar.Value) is { } domain) expected.Add(domain); else Issue("cpanel.domain_invalid", "sites", "An addon domain name could not be normalized.");
                }
            }
            catch (Exception error) when (error is YamlException || error is InvalidDataException dataError && dataError.Message.StartsWith("migration.cpanel.", StringComparison.Ordinal))
            { Issue("cpanel.domain_inventory_malformed", "sites", "The main userdata YAML inventory could not be parsed safely."); }
        }
        private async Task ReadSites(HashSet<string> expected)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in source.Files.Keys.Where(p => p.StartsWith("userdata/", StringComparison.Ordinal) && p.Count(c => c == '/') == 1).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var fileName = path[9..];
                if (fileName == "main" || fileName.EndsWith("_SSL", StringComparison.Ordinal) || fileName.EndsWith(".cache", StringComparison.Ordinal) || fileName.EndsWith(".yaml", StringComparison.Ordinal) || fileName.EndsWith(".json", StringComparison.Ordinal)) continue;
                var candidate = Host(fileName);
                if (candidate is null) { Issue("cpanel.userdata_unrecognized", "sites", "An unrecognized userdata entry needs manual inspection."); continue; }
                var id = "site:" + candidate;
                try
                {
                    var yaml = CpanelYaml.Read(await Text(path));
                    var domain = Host(CpanelYaml.Scalar(yaml, "servername"));
                    var home = UnixAbsolute(CpanelYaml.Scalar(yaml, "homedir"));
                    var docroot = UnixAbsolute(CpanelYaml.Scalar(yaml, "documentroot"));
                    if (domain != candidate || home is null || docroot is null || (docroot != home && !docroot.StartsWith(home + "/", StringComparison.Ordinal))) throw new InvalidDataException("migration.cpanel.docroot_invalid");
                    var relative = docroot == home ? "" : docroot[(home.Length + 1)..];
                    var logicalRoot = "homedir" + (relative.Length > 0 ? "/" + relative : "");
                    if (!source.Directories.Contains(logicalRoot)) { Issue("cpanel.site.content_missing", id, "The documented source document root is absent from the archive."); continue; }
                    var contentPrefix = "sites/" + (sites.Count + 1).ToString(CultureInfo.InvariantCulture);
                    var prefix = logicalRoot + "/";
                    var count = 0L;
                    foreach (var sourceFile in UnderFiles(logicalRoot))
                    {
                        if (SystemHomeMetadata(sourceFile.Key)) { Issue("cpanel.site.system_metadata_excluded", id, "cPanel password, mail or key metadata was excluded from document-root content."); continue; }
                        var fileRelative = sourceFile.Key[prefix.Length..];
                        if (!WindowsPayloadPath(fileRelative)) { Issue("cpanel.site.filename_unsupported", id, "A source filename cannot be represented safely in a Windows site payload."); continue; }
                        var target = contentPrefix + "/" + fileRelative;
                        await Payload(target, sourceFile.Value);
                        sourceToPayload.TryAdd(sourceFile.Key, target);
                        count++;
                    }
                    var aliases = CpanelYaml.Strings(yaml, "serveralias").Select(Host).Where(n => n is not null).Cast<string>().Append(domain).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var emptyDirectories = new List<string>();
                    foreach (var directory in UnderDirectories(logicalRoot))
                    {
                        if (SystemHomeMetadata(directory + "/") || UnderFiles(directory).Any()) continue;
                        var emptyRelative = directory[prefix.Length..];
                        if (!WindowsPayloadPath(emptyRelative)) { Issue("cpanel.site.filename_unsupported", id, "An empty source directory cannot be represented safely on Windows."); continue; }
                        emptyDirectories.Add(emptyRelative);
                    }
                    CountItems(1 + aliases.Count + emptyDirectories.Count);
                    var phpVersion = CpanelYaml.Scalar(yaml, "phpversion");
                    if (string.IsNullOrWhiteSpace(phpVersion)) Issue("cpanel.site.runtime_unknown", id, "The source PHP/runtime profile requires explicit destination selection.");
                    sites.Add(new() { Id = id, Name = domain, SourcePhysicalPath = docroot, ContentPath = contentPrefix, RuntimeProfile = phpVersion.Length > 0 ? "php" : "", PhpVersion = phpVersion, FileCount = count, EmptyDirectories = emptyDirectories, HostNames = aliases });
                    var site = sites[^1];
                    if (!siteByRoot.TryGetValue(site.SourcePhysicalPath, out var rootSites)) siteByRoot[site.SourcePhysicalPath] = rootSites = [];
                    rootSites.Add(site);
                    foreach (var host in site.HostNames)
                    {
                        if (!siteByHost.TryGetValue(host, out var hostSites)) siteByHost[host] = hostSites = [];
                        hostSites.Add(site.Id);
                    }
                    found.Add(domain);
                }
                catch (Exception error) when (error is YamlException || error is InvalidDataException dataError && dataError.Message.StartsWith("migration.cpanel.", StringComparison.Ordinal))
                { Issue("cpanel.site.userdata_malformed", id, "The domain userdata or source content could not be normalized safely."); }
            }
            foreach (var domain in expected.Where(d => !found.Contains(d))) Issue("cpanel.site.metadata_missing", "site:" + domain, "A declared domain has no readable, complete vhost/document-root metadata.");
            if (sites.Count == 0) Issue("cpanel.sites.unavailable", "sites", "No site was proven from userdata and its actual document root.");
        }
        private async Task ReadDatabases()
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in source.Files.Keys.Where(p => p.StartsWith("mysql/", StringComparison.Ordinal) || p.StartsWith("psql/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var mysql = path.StartsWith("mysql/", StringComparison.Ordinal);
                var fileName = path[(path.IndexOf('/') + 1)..];
                if (fileName.Contains('/')) { Issue("cpanel.database.layout_unsupported", "databases", "Nested database archive layout is unsupported."); continue; }
                if (!mysql && fileName is "1psql_users.sql" or "psql_grants.sql") continue; // Credentials/grants are intentionally not normalized.
                if (!(mysql && fileName.EndsWith(".sql", StringComparison.Ordinal) || !mysql && fileName.EndsWith(".tar", StringComparison.Ordinal)))
                { Issue("cpanel.database.format_unsupported", "databases", "An unrecognized database backup file requires manual mapping."); continue; }
                var name = fileName[..^4];
                var id = "db:" + (mysql ? "mysql:" : "postgresql:") + name;
                present.Add(name);
                if (new FileInfo(source.Files[path]).Length == 0) { Issue("cpanel.database.dump_empty", id, "A database dump is empty or only an unavailable placeholder."); continue; }
                var target = "databases/" + (databases.Count + 1).ToString(CultureInfo.InvariantCulture) + (mysql ? ".sql" : ".tar");
                await Payload(target, source.Files[path]);
                CountItems(1);
                databases.Add(new() { Id = id, Name = name, Engine = mysql ? "mysql" : "postgresql", DumpPath = target, Format = mysql ? "sql" : "tar" });
                Issue("cpanel.database.site_mapping_required", id, "The backup does not prove database-to-site ownership; select the destination site explicitly.");
                Issue("cpanel.database.credentials_required", id, "Create a new target database principal; source passwords and grants were not imported.");

            }
            foreach (var path in source.Files.Keys.Where(p => p.StartsWith("mysql-timestamps/", StringComparison.Ordinal) && p.EndsWith(".txt", StringComparison.Ordinal)))
            {
                var name = path[17..^4];
                if (!present.Contains(name)) Issue("cpanel.database.dump_missing", "db:mysql:" + name, "A database timestamp inventory entry has no corresponding SQL dump.");
            }
            if (!source.Directories.Contains("mysql") && !source.Directories.Contains("psql")) Issue("cpanel.database.inventory_unavailable", "databases", "Database directories are absent; zero databases was not assumed.");
        }
        private async Task ReadMail()
        {
            var names = source.Files.Keys.Where(p => p.StartsWith("homedir/etc/", StringComparison.Ordinal) && p.EndsWith("/passwd", StringComparison.Ordinal) && p.Count(c => c == '/') == 3)
                .Select(p => p.Split('/')[2]).Concat(source.Files.Keys.Where(p => p.StartsWith("va/", StringComparison.Ordinal) && p.Count(c => c == '/') == 1).Select(p => p[3..]))
                .Concat(source.Directories.Where(p => p.StartsWith("homedir/mail/", StringComparison.Ordinal) && p.Count(c => c == '/') == 2).Select(p => p.Split('/')[2])).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var raw in names)
            {
                token.ThrowIfCancellationRequested();
                var domain = Host(raw);
                if (domain is null) { Issue("cpanel.mail.domain_invalid", "mail", "An unrecognized mail domain requires manual inspection."); continue; }
                var id = "mail:" + domain;
                var accounts = new List<PortableMigrationMailAccount>();
                var aliases = new List<PortableMigrationMailAlias>();
                var passwd = "homedir/etc/" + raw + "/passwd";
                var known = new HashSet<string>(StringComparer.Ordinal);
                var quotas = new Dictionary<string, long>(StringComparer.Ordinal);
                var quotaPath = "homedir/etc/" + raw + "/quota";
                if (source.Files.ContainsKey(quotaPath))
                    foreach (var line in Lines(await Text(quotaPath)))
                    {
                        var split = line.Split(':');
                        if (split.Length == 2 && long.TryParse(split[1], out var bytes) && bytes >= 0) quotas[split[0]] = bytes == 0 ? 0 : bytes / (1024 * 1024) + (bytes % (1024 * 1024) == 0 ? 0 : 1);
                        else Issue("cpanel.mail.quota_malformed", id, "A mailbox quota record could not be normalized.");
                    }
                if (!source.Files.ContainsKey(passwd)) Issue("cpanel.mail.accounts_unavailable", id, "Mailbox account metadata is missing; mail directories alone are not an account inventory.");
                else foreach (var line in Lines(await Text(passwd)))
                {
                    var fields = line.Split(':');
                    var local = fields[0];
                    if (fields.Length < 7 || !Regex.IsMatch(local, "^[A-Za-z0-9_.+%-]{1,128}$", RegexOptions.CultureInvariant) || !known.Add(local))
                    { Issue("cpanel.mail.account_malformed", id, "A mailbox record is malformed or duplicated."); continue; }
                    var email = local + "@" + domain;
                    Issue("cpanel.mail.active_state_unknown", email, "The archive does not prove mailbox suspension/login state; choose the target state explicitly.");
                    Issue("cpanel.mail.new_password_required", email, "A new target mailbox password is required; source password hashes were excluded.");
                    if (!quotas.TryGetValue(local, out var quota)) Issue("cpanel.mail.quota_unknown", email, "The source mailbox quota is unavailable; choose a target quota explicitly.");
                    var mailRoot = "homedir/mail/" + raw + "/" + local;
                    var actualMaildir = source.Directories.Contains(mailRoot + "/cur") && source.Directories.Contains(mailRoot + "/new") && source.Directories.Contains(mailRoot + "/tmp");
                    if (!actualMaildir)
                    {
                        Issue("cpanel.mail.archive_missing", email, "A complete Maildir cur/new/tmp layout is missing; metadata is not message backup success.");
                        accounts.Add(new() { Email = email, QuotaMb = quota, EmailCount = null });
                        continue;
                    }
                    var archivePath = Path.Combine(stage, "mail-" + Guid.NewGuid().ToString("N") + ".zip");
                    var archiveResult = await PortableMaildirWriter.CreateAsync(source, mailRoot, archivePath, limits, token);
                    var targetArchive = "mail/" + (domains.Count + 1) + "/" + (accounts.Count + 1) + ".zip";
                    await Payload(targetArchive, archivePath);
                    var count = archiveResult.EmailCount;
                    accounts.Add(new() { Email = email, QuotaMb = quota, EmailCount = count, ArchivePath = targetArchive, ArchiveFormat = "maildir-zip" });
                }
                foreach (var directory in UnderDirectories("homedir/mail/" + raw).Where(p => p.Count(c => c == '/') == 3))
                    if (!known.Contains(directory.Split('/')[3])) Issue("cpanel.mail.orphan_maildir", id, "A Maildir has no proven mailbox account mapping.");
                var va = "va/" + raw;
                if (source.Files.ContainsKey(va))
                    foreach (var line in Lines(await Text(va)))
                    {
                        var colon = line.IndexOf(':');
                        var address = colon > 0 ? line[..colon].Trim() : "";
                        var destination = colon > 0 ? line[(colon + 1)..].Trim() : "";
                        if (address == "*" && destination is ":fail:" or ":blackhole:") continue;
                        if (HostEmail(address.Contains('@') ? address : address + "@" + domain) is not { } email || HostEmail(destination) is not { } target)
                        { Issue("cpanel.mail.forwarder_unsupported", id, "A catch-all, pipe, autoresponder or multi-target forwarder requires manual mapping."); continue; }
                        aliases.Add(new() { Address = email, Destination = target });
                    }
                CountItems(1 + accounts.Count + aliases.Count);
                domains.Add(new() { Id = id, Name = domain, SiteIds = SiteIds(domain), Accounts = accounts, Aliases = aliases });
            }
            if (!source.Directories.Contains("homedir/mail") || !source.Directories.Contains("homedir/etc")) Issue("cpanel.mail.inventory_unavailable", "mail", "Mail/account directories are absent; zero mailboxes was not assumed.");
        }
        private async Task ReadDns()
        {
            foreach (var path in source.Files.Keys.Where(p => p.StartsWith("dnszones/", StringComparison.Ordinal)))
            {
                var name = path[9..];
                if (!name.EndsWith(".db", StringComparison.Ordinal) || Host(name[..^3]) is not { } domain)
                { Issue("cpanel.dns.filename_unsupported", "dns", "An unrecognized DNS zone file requires manual mapping."); continue; }
                await Payload("source/dns/" + (zones.Count + 1) + ".zone", source.Files[path]);
                var zone = CpanelBindZoneReader.Read(domain, await Text(path), SiteIds(domain), Issue);
                CountItems(1 + zone.Records.Count);
                zones.Add(zone);
            }
            var knownZones = zones.Select(z => z.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var site in sites)
            {
                var candidate = site.Name;
                var covered = false;
                while (candidate.Contains('.'))
                {
                    if (knownZones.Contains(candidate)) { covered = true; break; }
                    candidate = candidate[(candidate.IndexOf('.') + 1)..];
                }
                if (!covered) Issue("cpanel.dns.zone_missing", "dns:" + site.Name, "A proven site has no corresponding source DNS zone or parent-zone coverage.");
            }
            if (!source.Directories.Contains("dnszones")) Issue("cpanel.dns.inventory_unavailable", "dns", "DNS zone inventory is absent; zero zones was not assumed.");
        }
        private async Task ReadTasks()
        {
            foreach (var path in source.Files.Keys.Where(p => p.StartsWith("cron/", StringComparison.Ordinal)))
            {
                var artifact = "source/tasks/" + (tasks.Count + 1) + ".cron";
                await Payload(artifact, source.Files[path]);
                foreach (var line in Lines(await Text(path)))
                {
                    var task = CpanelCronReader.Read(line, tasks.Count + 1, artifact, SiteIds, ScriptMapping, Issue);
                    CountItems(1 + task.ArtifactPaths.Count + task.SiteIds.Count);
                    tasks.Add(task);
                }
            }
            if (!source.Directories.Contains("cron")) Issue("cpanel.tasks.inventory_unavailable", "tasks", "The crontab inventory is absent; zero jobs was not assumed.");
        }
        private List<string> SiteIds(string domain) => siteByHost.TryGetValue(domain, out var ids) ? ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
        private (List<string> SiteIds, List<string> ArtifactPaths) ScriptMapping(string command)
        {
            var ids = new List<string>(); var paths = new List<string>();
            var directory = command;
            while (directory.LastIndexOf('/') is var slash && slash > 0)
            {
                directory = directory[..slash];
                if (!siteByRoot.TryGetValue(directory, out var rootSites)) continue;
                foreach (var site in rootSites)
                {
                    ids.Add(site.Id);
                    var payload = site.ContentPath + command[site.SourcePhysicalPath.Length..];
                    if (payloadNames.Contains(payload)) paths.Add(payload);
                }
            }
            return (ids, paths);
        }
        private IEnumerable<KeyValuePair<string,string>> UnderFiles(string root) => source.FilesUnder(root);
        private IEnumerable<string> UnderDirectories(string root) => source.DirectoriesUnder(root);
        private void CountItems(int count)
        {
            itemCount += count;
            if (itemCount > limits.MaxItems) throw new InvalidDataException("migration.source.item_limit");
        }
        private void Issue(string code, string resourceId, string detail)
        {
            token.ThrowIfCancellationRequested();
            if (!issueKeys.Add((code, resourceId, detail))) return;
            CountItems(1);
            if (issues.Count >= limits.MaxItems) throw new InvalidDataException("migration.source.item_limit");
            issues.Add(new() { Code = code, ResourceId = resourceId, Detail = detail });
        }
        private async Task Payload(string logical, string disk)
        {
            if (Payloads.Count >= limits.MaxItems) throw new InvalidDataException("migration.source.item_limit");
            var description = await PortablePackageWriter.DescribeAsync(logical, disk, token);
            if (description.Size > limits.MaxPayloadBytes || description.Size > limits.MaxTotalUncompressedBytes - normalizedBytes) throw new InvalidDataException("migration.source.payload_limit");
            normalizedBytes += description.Size;
            CountItems(1);
            if (!payloadNames.Add(logical)) throw new InvalidDataException("migration.source.duplicate_payload");
            Payloads.Add((logical, disk)); descriptions.Add(description);
        }
        private async Task<string> Text(string logical)
        {
            token.ThrowIfCancellationRequested();
            var disk = source.Files[logical];
            if (new FileInfo(disk).Length > limits.MaxManifestBytes) throw new InvalidDataException("migration.cpanel.metadata_size_limit");
            using var reader = new StreamReader(disk, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(token);
        }
        private static IEnumerable<string> Lines(string value) => value.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#'));
        private static bool SystemHomeMetadata(string key) => new[] { "homedir/etc/", "homedir/mail/", "homedir/ssl/", "homedir/.cpanel/" }.Any(p => key.StartsWith(p, StringComparison.Ordinal));
        private static string? Host(string? value)
        {
            value = value?.Trim().TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(value) || value.Length > 253 || !value.Contains('.') || value.Split('.').Any(p => p.Length is 0 or > 63 || p.StartsWith('-') || p.EndsWith('-') || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) return null;
            return value;
        }
        private static string? HostEmail(string value)
        {
            var at = value.LastIndexOf('@');
            return at > 0 && Regex.IsMatch(value[..at], "^[A-Za-z0-9_.+%-]{1,128}$", RegexOptions.CultureInvariant) && Host(value[(at + 1)..]) is { } domain ? value[..at] + "@" + domain : null;
        }
        private static string? UnixAbsolute(string value)
        {
            if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\') || value.Any(char.IsControl)) return null;
            value = value.TrimEnd('/');
            return value.Length > 0 && !value[1..].Split('/').Any(p => p is "" or "." or "..") ? value : null;
        }
        private static bool WindowsPayloadPath(string value)
            => value.Length <= 900 && value.Split('/').All(p => p.Length is > 0 and <= 255 && !p.EndsWith('.') && !p.EndsWith(' ') && p.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) < 0
                && !Regex.IsMatch(p.Split('.')[0].TrimEnd(' '), @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }
}
