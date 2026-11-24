using System.Net;
using System.Net.Sockets;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Sources;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace AdaPanel.Infrastructure.Migration.Full;

internal interface IMigrationMailImap
{
    Task ProbeAsync(MigrationMailEndpoint endpoint, CancellationToken token);
    Task<IMigrationMailboxSession> OpenAsync(MigrationMailEndpoint endpoint, string address, string password, string scratch, CancellationToken token);
    Task VerifyDisabledAsync(MigrationMailEndpoint endpoint, string address, string password, CancellationToken token);
}
internal interface IMigrationMailboxSession : IAsyncDisposable
{
    Task ImportAndVerifyAsync(MigrationMailDataset source, CancellationToken token);
}

/// <summary>Mailbox credentials only. The socket is local; TLS validates the configured mail identity.</summary>
internal sealed class MigrationMailImap : IMigrationMailImap
{
    private readonly Func<ImapClient> clients;
    internal MigrationMailImap(Func<ImapClient>? clients = null) => this.clients = clients ?? (() => new ImapClient());
    private async Task<ImapClient> ConnectAsync(MigrationMailEndpoint endpoint, CancellationToken token)
    {
        if (Uri.CheckHostName(endpoint.Host) != UriHostNameType.Dns || !endpoint.Host.Contains('.') || endpoint.Port is < 1 or > 65535) throw Error("imap_identity_invalid");
        var client = clients(); client.Timeout = 60_000;
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, endpoint.Port, token);
            // No certificate callback or AllowInvalidCertificates option is installed in production.
            await client.ConnectAsync(socket, endpoint.Host, endpoint.Port,
                endpoint.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, token);
            if ((client.Capabilities & (ImapCapabilities.UidPlus | ImapCapabilities.ObjectID)) != (ImapCapabilities.UidPlus | ImapCapabilities.ObjectID))
                throw Error("imap_verification_capability_missing");
            return client;
        }
        catch { socket.Dispose(); client.Dispose(); throw; }
    }
    public async Task ProbeAsync(MigrationMailEndpoint endpoint, CancellationToken token)
    { using var client = await ConnectAsync(endpoint, token); await client.DisconnectAsync(false, token); }
    public async Task<IMigrationMailboxSession> OpenAsync(MigrationMailEndpoint endpoint, string address, string password, string scratch, CancellationToken token)
    {
        var client = await ConnectAsync(endpoint, token);
        try { await client.AuthenticateAsync(new SaslMechanismPlain(address, password), token); return new Session(client, scratch); }
        catch { client.Dispose(); throw; }
    }
    public async Task VerifyDisabledAsync(MigrationMailEndpoint endpoint, string address, string password, CancellationToken token)
    {
        using var client = await ConnectAsync(endpoint, token);
        try { await client.AuthenticateAsync(new SaslMechanismPlain(address, password), token); }
        catch (AuthenticationException) { return; }
        // A network/TLS/unknown failure is not evidence that the newly-disabled password was rejected.
        throw Error("account_disable_unverified");
    }
    private sealed class Session(ImapClient client, string scratch) : IMigrationMailboxSession
    {
        private readonly Dictionary<string, IMailFolder> folders = new(StringComparer.Ordinal);
        public async Task ImportAndVerifyAsync(MigrationMailDataset source, CancellationToken token)
        {
            var initial = await AllFoldersAsync(token);
            foreach (var folder in initial.Where(folder => (folder.Attributes & FolderAttributes.NoSelect) == 0))
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, token);
                if (folder.Count != 0) throw Error("target_not_empty");
                await folder.CloseAsync(false, token);
            }
            foreach (var specification in source.Folders.OrderBy(folder => folder.Path.Count(character => character == '/')).ThenBy(folder => folder.Path, StringComparer.Ordinal))
            {
                var folder = await FolderAsync(specification, token);
                if (specification.Subscribed == null) continue; // Unknown source state is a required action, not an invented subscription.
                if (specification.Subscribed.Value) await folder.SubscribeAsync(token); else await folder.UnsubscribeAsync(token);
                if (folder.IsSubscribed != specification.Subscribed) throw Error("folder_subscription_unverified");
            }
            foreach (var message in source.Messages)
            {
                token.ThrowIfCancellationRequested();
                var folder = folders[message.Folders[0]];
                await folder.OpenAsync(FolderAccess.ReadWrite, token);
                await using var input = File.OpenRead(message.BodyPath);
                using var mime = await MimeMessage.LoadAsync(new ParserOptions { MaxMimeDepth = 64 }, input, persistent: true, token);
                var uid = await folder.AppendAsync(MigrationMailArchiveReader.Format(), new AppendRequest(mime, message.Flags, message.Keywords, message.Date), token);
                if (!uid.HasValue) throw Error("append_uid_unverified");
                foreach (var additional in message.Folders.Skip(1))
                {
                    var destination = folders[additional];
                    var copied = await folder.CopyToAsync(uid.Value, destination, token);
                    if (!copied.HasValue) throw Error("copy_uid_unverified");
                }
                await folder.CloseAsync(false, token); // Never EXPUNGE preserved deleted flags.
            }
            var actualUnique = new Dictionary<string, (string Print, HashSet<string> Paths)>(StringComparer.Ordinal);
            foreach (var folder in await AllFoldersAsync(token))
            {
                if ((folder.Attributes & FolderAttributes.NoSelect) != 0) continue;
                var sourcePaths = folders.Where(pair => pair.Value.FullName == folder.FullName).Select(pair => pair.Key).ToArray();
                if (sourcePaths.Length > 1) throw Error("folder_mapping_collision");
                var expected = sourcePaths.Length == 0 ? [] : source.Messages.Where(message => message.Folders.Contains(sourcePaths[0], StringComparer.Ordinal)).ToArray();
                await folder.OpenAsync(FolderAccess.ReadOnly, token);
                if (folder.Count != expected.Length) throw Error("message_count_unverified");
                var expectedPrints = expected.Select(Fingerprint).GroupBy(print => print, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                if (folder.Count > 0)
                {
                    var summaries = await folder.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.EmailId, token);
                    if (summaries.Count != expected.Length) throw Error("message_count_unverified");
                    foreach (var summary in summaries)
                    {
                        if (summary.EmailId == null || summary.InternalDate == null || summary.Flags == null) throw Error("message_metadata_unmeasured");

                        var path = Path.Combine(scratch, "verify-" + Guid.NewGuid().ToString("N") + ".eml");
                        try
                        {
                            await using (var input = await folder.GetStreamAsync(summary.UniqueId, token))
                            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                await SourceArchiveReader.CopyAsync(input, output, 512L * 1024 * 1024, token);
                            var hash = await MigrationMailArchiveReader.CanonicalHashAsync(path, token);
                            var fingerprint = Print(hash, summary.InternalDate.Value, summary.Flags.Value, summary.Keywords);
                            if (!actualUnique.TryGetValue(summary.EmailId, out var logical))
                                actualUnique.Add(summary.EmailId, logical = (fingerprint, new HashSet<string>(StringComparer.Ordinal)));
                            if (logical.Print != fingerprint || sourcePaths.Length != 1 || !logical.Paths.Add(sourcePaths[0])) throw Error("message_membership_unverified");
                            if (!expectedPrints.TryGetValue(fingerprint, out var remaining) || remaining == 0) throw Error("message_content_or_metadata_unverified");
                            expectedPrints[fingerprint] = remaining - 1;
                        }
                        finally { if (File.Exists(path)) File.Delete(path); }
                    }
                }
                await folder.CloseAsync(false, token);
                if (expectedPrints.Values.Any(remaining => remaining != 0)) throw Error("message_content_or_metadata_unverified");
            }
            if (actualUnique.Count != source.Messages.Count) throw Error("unique_message_count_unverified");
            var expectedMemberships = source.Messages.Select(message => Fingerprint(message) + "|" + System.Text.Json.JsonSerializer.Serialize(message.Folders.Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal);
            var actualMemberships = actualUnique.Values.Select(message => message.Print + "|" + System.Text.Json.JsonSerializer.Serialize(message.Paths.Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal);
            if (!expectedMemberships.SequenceEqual(actualMemberships, StringComparer.Ordinal)) throw Error("message_membership_unverified");
        }
        private async Task<IMailFolder> FolderAsync(MigrationMailFolder source, CancellationToken token)
        {
            if (folders.TryGetValue(source.Path, out var ready)) return ready;
            IMailFolder? existing = source.Role switch
            {
                "inbox" => client.Inbox, "sent" => client.GetFolder(SpecialFolder.Sent), "drafts" => client.GetFolder(SpecialFolder.Drafts),
                "trash" => client.GetFolder(SpecialFolder.Trash), "junk" => client.GetFolder(SpecialFolder.Junk), "archive" => client.GetFolder(SpecialFolder.Archive), _ => null
            };
            if (source.Path == "INBOX") existing = client.Inbox;
            if (existing == null)
            {
                var slash = source.Path.LastIndexOf('/');
                var parent = slash < 0 ? client.GetFolder(client.PersonalNamespaces[0])
                    : folders.TryGetValue(source.Path[..slash], out var mapped) ? mapped : await FolderAsync(new(source.Path[..slash], "", true), token);
                var name = slash < 0 ? source.Path : source.Path[(slash + 1)..];
                try { existing = await parent.GetSubfolderAsync(name, token); }
                catch (FolderNotFoundException)
                {
                    var role = source.Role switch { "sent" => SpecialFolder.Sent, "drafts" => SpecialFolder.Drafts, "trash" => SpecialFolder.Trash, "junk" => SpecialFolder.Junk, "archive" => SpecialFolder.Archive, _ => (SpecialFolder?)null };
                    existing = role.HasValue ? await parent.CreateAsync(name, role.Value, token) : await parent.CreateAsync(name, true, token);
                }
            }
            if (existing == null) throw Error("folder_creation_unverified");
            if (folders.Values.Any(folder => folder.FullName == existing.FullName)) throw Error("folder_mapping_collision");
            folders.Add(source.Path, existing); return existing;
        }
        private async Task<IReadOnlyList<IMailFolder>> AllFoldersAsync(CancellationToken token)
        {
            var result = new List<IMailFolder>();
            foreach (var space in client.PersonalNamespaces)
                result.AddRange(await client.GetFoldersAsync(space, StatusItems.None, false, token));
            if (!result.Any(folder => folder.FullName == client.Inbox.FullName)) result.Add(client.Inbox);
            return result.GroupBy(folder => folder.FullName, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        }
        private static string Fingerprint(MigrationMailMessage message) => Print(message.CanonicalHash, message.Date, message.Flags, message.Keywords);
        private static string Print(string hash, DateTimeOffset date, MessageFlags flags, IEnumerable<string> keywords) => hash + ":" + date.ToUnixTimeSeconds() + ":" + (int)(flags & (MessageFlags.Seen | MessageFlags.Answered | MessageFlags.Flagged | MessageFlags.Deleted | MessageFlags.Draft)) + ":" + string.Join(',', keywords.Select(keyword => keyword.ToLowerInvariant()).Order(StringComparer.Ordinal));
        public async ValueTask DisposeAsync() { try { if (client.IsConnected) await client.DisconnectAsync(false); } finally { client.Dispose(); } }
    }
    private static AppException Error(string code) => new("migration.mail_" + code, 409, 409);
}
