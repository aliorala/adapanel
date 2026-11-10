using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using MailKit;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed class MigrationMailTargetOptions
{
    public int MaxMessages { get; set; } = 1_000_000;
    public int MaxFolderMemberships { get; set; } = 2_000_000;
    public int MaxFolders { get; set; } = 10_000;
    public long MaxMessageBytes { get; set; } = 512L * 1024 * 1024;
}
internal sealed record MigrationMailFolder(string Path, string Role, bool? Subscribed);
internal sealed record MigrationMailMessage(string BodyPath, string CanonicalHash, DateTimeOffset Date,
    MessageFlags Flags, IReadOnlyList<string> Keywords, IReadOnlyList<string> Folders);
internal sealed class MigrationMailDataset
{
    internal List<MigrationMailFolder> Folders { get; } = [];
    internal List<MigrationMailMessage> Messages { get; } = [];
    internal bool HasNonMailData { get; set; }
    internal bool SubscriptionStateUnverified { get; set; }
    internal bool HasSubsecondDates => Messages.Any(message => message.Date.Ticks % TimeSpan.TicksPerSecond != 0);
}

/// <summary>Both formats become the same disk-backed message stream; no source pathname is materialized.</summary>
internal sealed class MigrationMailArchiveReader(IMigrationPackageStore packages, PortableMigrationPackageOptions limits,
    MigrationMailTargetOptions mailLimits, string stage)
{
    private long bodyBytes;
    private int messages, memberships;
    private PleskArchiveReader? zipArchives;
    internal async Task<MigrationMailDataset> ReadAsync(Guid packageId, PortableMigrationMailAccount account,
        PortableMigrationManifest manifest, CancellationToken token)
    {
        var payload = manifest.Payloads.SingleOrDefault(payload => payload.Path == account.ArchivePath) ?? throw Error("archive_missing");
        var archive = Path.Combine(stage, "archive-" + Guid.NewGuid().ToString("N"));
        await using (var input = await packages.OpenPayloadAsync(packageId, payload.Path, token))
        await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072]; long copied = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, token); if (count == 0) break;
                if (count > payload.Size - copied || count > limits.MaxPayloadBytes - copied) throw Error("archive_changed");
                hash.AppendData(buffer.AsSpan(0, count)); await output.WriteAsync(buffer.AsMemory(0, count), token); copied += count;
            }
            if (copied != payload.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase)) throw Error("archive_changed");
        }
        var result = account.ArchiveFormat switch
        {
            "maildir-zip" => await MaildirAsync(archive, token),
            "vandelay-sqlite" => await SqliteAsync(archive, account.Email, token),
            _ => throw Error("archive_format_unsupported")
        };
        if (account.EmailCount.HasValue && account.EmailCount.Value != result.Messages.Count) throw Error("source_count_mismatch");
        return result;
    }
    private async Task<MigrationMailDataset> MaildirAsync(string archive, CancellationToken token)
    {
        var inventory = await (zipArchives ??= new PleskArchiveReader(stage, limits, token)).ExtractAsync(archive, "zip");
        if (!new[] { "cur", "new", "tmp" }.All(inventory.Directories.Contains)) throw Error("maildir_layout_unproven");
        if (!inventory.Files.TryGetValue(PortableMaildirWriter.MetadataEntryName, out var sidecar)) throw Error("source_metadata_missing");
        if (new FileInfo(sidecar).Length > limits.MaxManifestBytes) throw Error("source_metadata_limit");
        await using var metadataInput = File.OpenRead(sidecar);
        using var metadata = await JsonDocument.ParseAsync(metadataInput, new JsonDocumentOptions { MaxDepth = 16 }, token);
        var root = metadata.RootElement;
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("format").GetString() != "maildir-source-metadata") throw Error("source_metadata_unsupported");
        var entries = root.GetProperty("files").EnumerateArray().ToArray();
        if (entries.Length > limits.MaxItems || entries.Length != inventory.Files.Count - 1) throw Error("source_metadata_mismatch");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new MigrationMailDataset();
        var folders = new HashSet<string>(StringComparer.Ordinal) { "INBOX" };
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var path = entry.GetProperty("path").GetString() ?? throw Error("source_metadata_mismatch");
            SourceArchiveReader.ValidateLogicalPath(path);
            if (!seen.Add(path) || !inventory.Files.TryGetValue(path, out var file)) throw Error("source_metadata_mismatch");
            var parts = path.Split('/');
            var isMessage = parts.Length >= 2 && parts[^2] is "cur" or "new";
            if (entry.GetProperty("isMessage").GetBoolean() != isMessage) throw Error("source_metadata_mismatch");
            if (!isMessage) continue;
            var date = entry.GetProperty("sourceDateUtc").GetDateTimeOffset();
            if (date.Offset != TimeSpan.Zero) throw Error("source_date_invalid");
            var flagsText = entry.GetProperty("maildirFlags").GetString() ?? "";
            var colon = parts[^1].LastIndexOf(":2,", StringComparison.Ordinal);
            var actualFlags = colon < 0 ? "" : parts[^1][(colon + 3)..];
            if (flagsText != actualFlags || flagsText.Any(flag => !"DFPRST".Contains(flag))) throw Error("source_flags_invalid");
            var flags = MessageFlags.None;
            if (flagsText.Contains('S')) flags |= MessageFlags.Seen;
            if (flagsText.Contains('R')) flags |= MessageFlags.Answered;
            if (flagsText.Contains('F')) flags |= MessageFlags.Flagged;
            if (flagsText.Contains('T')) flags |= MessageFlags.Deleted;
            if (flagsText.Contains('D')) flags |= MessageFlags.Draft;
            var folder = MaildirFolder(string.Join('/', parts[..^2])); folders.Add(folder);
            await AddMessageAsync(result, file, date, flags, flagsText.Contains('P') ? ["$Forwarded"] : [], [folder], token);
        }
        foreach (var directory in inventory.Directories)
        {
            var pieces = directory.Split('/');
            if (pieces[^1] is "cur" or "new" or "tmp") folders.Add(MaildirFolder(string.Join('/', pieces[..^1])));
        }
        if (folders.Count > mailLimits.MaxFolders) throw Error("folder_limit");
        var subscribed = await MaildirSubscriptionsAsync(inventory, seen, folders, token);
        result.SubscriptionStateUnverified = subscribed == null;
        result.Folders.AddRange(folders.Order(StringComparer.Ordinal).Select(folder => new MigrationMailFolder(folder, folder == "INBOX" ? "inbox" : "", subscribed == null ? null : subscribed.Contains(folder))));
        return result;
    }
    private async Task<HashSet<string>?> MaildirSubscriptionsAsync(SourceArchiveInventory inventory, HashSet<string> metadataPaths, HashSet<string> folders, CancellationToken token)
    {
        // Dovecot stores a newline list (or V\t2 header + tab-separated components).
        // Courier uses INBOX-prefixed names. Only captured, bounded files whose names
        // resolve uniquely to actual archive folders establish subscription provenance.
        HashSet<string>? combined = null;
        foreach (var name in new[] { "subscriptions", "courierimapsubscribed" })
        {
            if (!inventory.Files.TryGetValue(name, out var file)) continue;
            if (!metadataPaths.Contains(name) || new FileInfo(file).Length > limits.MaxManifestBytes) return null;
            string contents;
            try { contents = await File.ReadAllTextAsync(file, new System.Text.UTF8Encoding(false, true), token); }
            catch (System.Text.DecoderFallbackException) { return null; }
            contents = contents.Replace("\r\n", "\n", StringComparison.Ordinal);
            var version2 = name == "subscriptions" && contents.StartsWith("V\t2\n\n", StringComparison.Ordinal);
            if (version2) contents = contents[5..];
            var lines = contents.Split('\n');
            if (lines.Length > mailLimits.MaxFolders + 1) return null;
            var parsed = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (index == lines.Length - 1 && line.Length == 0) continue;
                if (line.Length is 0 or > 4096 || line.Any(character => char.IsControl(character) && !(version2 && character == '\t')) || line.Contains('&') || line.Contains('\\')) return null;
                if (name == "courierimapsubscribed" && line.StartsWith("INBOX.", StringComparison.OrdinalIgnoreCase)) line = line[6..];
                var candidates = version2 ? new[] { string.Join('/', line.Split('\t')) } : new[] { line, line.Replace('.', '/') };
                if (line.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) candidates = ["INBOX"];
                var matches = candidates.Distinct(StringComparer.Ordinal).Where(folders.Contains).ToArray();
                if (matches.Length != 1 || !parsed.Add(matches[0])) return null;
            }
            if (combined != null && !combined.SetEquals(parsed)) return null;
            combined = parsed;
        }
        return combined;
    }
    private async Task<MigrationMailDataset> SqliteAsync(string archive, string sourceEmail, CancellationToken token)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = archive, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ConnectionString);
        await connection.OpenAsync(token);
        await using (var safety = connection.CreateCommand()) { safety.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;"; await safety.ExecuteNonQueryAsync(token); }
        foreach (var (table, expected) in new[]
        {
            ("sources", new[] { "id", "kind", "session_url", "account_id", "account_name", "username" }),
            ("blobs", new[] { "id", "hash", "data" }),
            ("mailboxes", new[] { "id", "name", "parent_id", "role", "sort_order", "is_subscribed" }),
            ("emails", new[] { "id", "blob_id", "received_at", "mailbox_ids", "keywords" })
        })
        {
            await using var type = connection.CreateCommand(); type.CommandText = "SELECT type, sql, length(sql) FROM sqlite_schema WHERE name=@name"; type.Parameters.AddWithValue("@name", table);
            await using (var rows = await type.ExecuteReaderAsync(token))
                if (!await rows.ReadAsync(token) || rows.GetString(0) != "table" || rows.GetInt64(2) > limits.MaxManifestBytes || rows.GetString(1).Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase)) throw Error("native_schema_unsupported");
            await using var columns = connection.CreateCommand(); columns.CommandText = "PRAGMA table_info(" + table + ")";
            var names = new List<string>(); var integerPrimaryKey = false;
            await using (var rows = await columns.ExecuteReaderAsync(token)) while (await rows.ReadAsync(token))
            { var name = rows.GetString(1); names.Add(name); if (name == "id") integerPrimaryKey = rows.GetString(2).Equals("INTEGER", StringComparison.OrdinalIgnoreCase) && rows.GetInt32(5) == 1; }
            if (!integerPrimaryKey) throw Error("native_schema_unsupported");
            if (expected.Any(name => !names.Contains(name, StringComparer.Ordinal)) || names.Any(name => !expected.Contains(name, StringComparer.Ordinal) && !(table == "emails" && name == "message_match"))) throw Error("native_schema_unsupported");
        }
        await using (var owner = connection.CreateCommand())
        {
            owner.CommandText = "SELECT kind, account_name, length(kind), length(account_name) FROM sources"; await using var rows = await owner.ExecuteReaderAsync(token);
            if (!await rows.ReadAsync(token) || rows.IsDBNull(1) || rows.GetInt64(2) > 32 || rows.GetInt64(3) > 254 || rows.GetString(0) != "jmap" || !rows.GetString(1).Equals(sourceEmail, StringComparison.OrdinalIgnoreCase) || await rows.ReadAsync(token)) throw Error("native_account_scope_invalid");
        }
        var folderRows = new Dictionary<long, (string Name, long? Parent, string Role, bool Subscribed)>();
        await using (var folderQuery = connection.CreateCommand())
        {
            folderQuery.CommandText = "SELECT id,name,parent_id,role,is_subscribed,length(name),length(role) FROM mailboxes";
            await using var rows = await folderQuery.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
            {
                if (folderRows.Count >= mailLimits.MaxFolders) throw Error("folder_limit");
                if (rows.GetInt64(5) > 255 || !rows.IsDBNull(6) && rows.GetInt64(6) > 32) throw Error("folder_name_unsupported");
                var name = rows.GetString(1);
                if (name.Length is 0 or > 255 || name.Contains('/') || name.Any(char.IsControl) || name is "." or "..") throw Error("folder_name_unsupported");
                var subscribed = rows.GetInt32(4); if (subscribed is not (0 or 1)) throw Error("native_schema_unsupported");
                var role = rows.IsDBNull(3) ? "" : rows.GetString(3);
                if (role is not ("" or "inbox" or "sent" or "drafts" or "trash" or "junk" or "archive")) throw Error("native_folder_role_unsupported");
                folderRows.Add(rows.GetInt64(0), (name, rows.IsDBNull(2) ? null : rows.GetInt64(2), role, subscribed == 1));
            }
        }
        var paths = new Dictionary<long, string>();
        if (folderRows.Count == 0 || !folderRows.Values.Any(folder => folder.Role == "inbox")) throw Error("native_folder_inventory_missing");
        if (folderRows.Values.Where(folder => folder.Role.Length != 0).GroupBy(folder => folder.Role, StringComparer.Ordinal).Any(group => group.Count() > 1)) throw Error("native_folder_role_unsupported");
        string Resolve(long id, HashSet<long> stack)
        {
            if (paths.TryGetValue(id, out var cached)) return cached;
            if (!stack.Add(id) || stack.Count > 64 || !folderRows.TryGetValue(id, out var folder)) throw Error("native_folder_hierarchy_invalid");
            var path = folder.Role == "inbox" ? "INBOX" : folder.Parent.HasValue ? Resolve(folder.Parent.Value, stack) + "/" + folder.Name : folder.Name;
            stack.Remove(id); paths.Add(id, path); return path;
        }
        foreach (var id in folderRows.Keys) Resolve(id, []);
        if (paths.Values.Distinct(StringComparer.Ordinal).Count() != paths.Count) throw Error("native_folder_hierarchy_invalid");
        var result = new MigrationMailDataset();
        result.Folders.AddRange(folderRows.Select(folder => new MigrationMailFolder(paths[folder.Key], folder.Value.Role, folder.Value.Subscribed)));
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT id,blob_id,received_at,mailbox_ids,keywords,length(received_at),length(mailbox_ids),length(keywords) FROM emails ORDER BY id";
            await using var rows = await query.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
            {
                if (rows.GetInt64(5) > 128 || rows.GetInt64(6) > limits.MaxManifestBytes || rows.GetInt64(7) > 65536) throw Error("native_metadata_limit");
                if (!DateTimeOffset.TryParse(rows.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)) throw Error("source_date_invalid");
                var folderIds = JsonSerializer.Deserialize<long[]>(rows.GetString(3)) ?? throw Error("native_folder_membership_invalid");
                if (folderIds.Length == 0 || folderIds.Distinct().Count() != folderIds.Length || folderIds.Any(id => !paths.ContainsKey(id))) throw Error("native_folder_membership_invalid");
                var keywords = JsonSerializer.Deserialize<string[]>(rows.GetString(4)) ?? throw Error("source_flags_invalid");
                if (keywords.Any(keyword => keyword == null) || keywords.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keywords.Length) throw Error("source_flags_invalid");
                var flags = MessageFlags.None; var custom = new List<string>();
                foreach (var keyword in keywords)
                {
                    flags |= keyword.ToLowerInvariant() switch
                    { "$seen" => MessageFlags.Seen, "$answered" => MessageFlags.Answered, "$flagged" => MessageFlags.Flagged, "$draft" => MessageFlags.Draft, "$deleted" => MessageFlags.Deleted, _ => MessageFlags.None };
                    if (keyword.ToLowerInvariant() is not ("$seen" or "$answered" or "$flagged" or "$draft" or "$deleted"))
                    { ValidateKeyword(keyword); custom.Add(keyword); }
                }
                var body = Path.Combine(stage, "message-" + Guid.NewGuid().ToString("N") + ".eml");
                using (var blob = new SqliteBlob(connection, "blobs", "data", rows.GetInt64(1), readOnly: true))
                await using (var output = new FileStream(body, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await SourceArchiveReader.CopyAsync(blob, output, mailLimits.MaxMessageBytes, token);
                await AddMessageAsync(result, body, date.ToUniversalTime(), flags, custom, folderIds.Select(id => paths[id]).ToArray(), token);
            }
        }
        foreach (var table in new[] { "identities", "sieve_scripts", "calendars", "calendar_events", "address_books", "contact_cards", "file_nodes" })
        {
            await using var exists = connection.CreateCommand(); exists.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name=@name"; exists.Parameters.AddWithValue("@name", table);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(token)) != 1) continue;
            await using var count = connection.CreateCommand(); count.CommandText = "SELECT EXISTS(SELECT 1 FROM " + table + " LIMIT 1)";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(token)) != 0) result.HasNonMailData = true;
        }
        return result;
    }
    private async Task AddMessageAsync(MigrationMailDataset dataset, string path, DateTimeOffset date, MessageFlags flags,
        IReadOnlyList<string> keywords, IReadOnlyList<string> folders, CancellationToken token)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > mailLimits.MaxMessageBytes || length > limits.MaxTotalUncompressedBytes - bodyBytes || ++messages > mailLimits.MaxMessages || folders.Count > mailLimits.MaxFolderMemberships - memberships) throw Error("message_limit");
        bodyBytes += length; memberships += folders.Count;
        var canonicalHash = await CanonicalHashAsync(path, token);
        dataset.Messages.Add(new(path, canonicalHash, date, flags, keywords, folders));
    }
    internal static FormatOptions Format() { var format = FormatOptions.Default.Clone(); format.NewLineFormat = NewLineFormat.Dos; return format; }
    internal static async Task<string> CanonicalHashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        using var message = await MimeMessage.LoadAsync(new ParserOptions { MaxMimeDepth = 64 }, input, persistent: true, token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var sink = new HashSink(hash);
        await message.WriteToAsync(Format(), sink, token);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string MaildirFolder(string prefix)
    {
        if (prefix.Length == 0) return "INBOX";
        var parts = prefix.StartsWith('.') ? prefix[1..].Split('.') : prefix.Split('/');
        if (parts.Any(part => part.Length == 0 || part.Any(char.IsControl) || part.Contains('/') || part is "." or "..")) throw Error("folder_name_unsupported");
        return string.Join('/', parts);
    }
    private static void ValidateKeyword(string keyword)
    { if (keyword.Length is 0 or > 255 || keyword.Any(character => character < 33 || character > 126 || "(){%*\"\\]".Contains(character))) throw Error("source_flags_invalid"); }
    private static AppException Error(string code) => new("migration.mail_" + code, 409, 409);
    private sealed class HashSink(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer.Span); return ValueTask.CompletedTask; }
        public override void Flush() { } public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
