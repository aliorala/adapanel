using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Full;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using AdaPanel.Infrastructure.Options;
using MailKit;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationMailTargetTests
{
    private static readonly DateTimeOffset SourceDate = new(2022, 2, 3, 4, 5, 6, TimeSpan.Zero);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maildir_and_official_shape_native_sqlite_use_the_same_scoped_verified_message_boundary(bool native)
    {
        using var f = new Fixture(native: native);
        var check = await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default);
        Assert.Empty(check.Blockers); Assert.Empty(f.Provisioner.Mutations);
        var credentials = f.Target.PrepareCredentials(f.Item, f.Manifest);
        var password = Assert.Single(credentials).Password;
        Assert.Equal("alice@target.test", credentials[0].UserName); Assert.Equal("mail.target.test", credentials[0].Host);
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, credentials, default);
        Assert.True(result.Verified); Assert.False(result.PartialStatePossible); Assert.Empty(result.ErrorCode);
        Assert.Equal(1, f.Imap.ImportCalls); var message = Assert.Single(f.Imap.Imported!.Messages);
        Assert.Equal(SourceDate, message.Date); Assert.True(message.Flags.HasFlag(MessageFlags.Seen));
        Assert.True(message.Flags.HasFlag(MessageFlags.Answered));
        Assert.All(f.Imap.Imported.Messages, source => Assert.DoesNotContain(':', Path.GetFileName(source.BodyPath)));
        if (native) Assert.Equal(2, message.Folders.Count); // One logical email, two mailbox memberships.
        else Assert.Contains(f.Imap.Imported.Folders, folder => folder.Path == "Empty");
        Assert.DoesNotContain(password, result.ErrorCode + string.Join(',', result.Evidence) + string.Join(',', result.CreatedResources));
        Assert.Contains("migration.mail_dns_dkim_smtp_cutover_required", result.RequiredActions);
        f.AssertClean();
    }

    [Fact]
    public async Task Existing_domain_is_never_merged_and_no_credentials_or_mail_are_changed()
    {
        using var f = new Fixture(); f.Provisioner.Exists = true;
        var check = await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default);
        Assert.Contains("migration.target_mail_domain_exists", check.Blockers);
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, [], default);
        Assert.False(result.Verified); Assert.Empty(f.Provisioner.Mutations); Assert.Equal(0, f.Imap.ImportCalls);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("ready")]
    [InlineData("endpoint")]
    public async Task Unmeasured_target_cannot_become_an_empty_ready_target(string missing)
    {
        using var f = new Fixture(); f.Provisioner.Missing = missing;
        Assert.NotEmpty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        Assert.Empty(f.Provisioner.Mutations); Assert.Equal(0, f.Imap.ImportCalls);
    }

    [Fact]
    public async Task Source_inactive_imports_temporarily_active_then_denies_login_receive_and_send()
    {
        using var f = new Fixture(active: false); var credentials = await f.Prepare();
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, credentials, default);
        Assert.True(result.Verified); Assert.False(f.Provisioner.Accounts[0].Active);
        Assert.Equal([true, false, false], f.Provisioner.ActiveRequests);
        Assert.Equal(2, f.Imap.DisabledChecks);
    }

    [Fact]
    public async Task A_silently_ignored_disable_or_quota_update_cannot_report_success()
    {
        using var f = new Fixture(active: false); var credentials = await f.Prepare();
        f.Provisioner.IgnoreDisable = true;
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, credentials, default);
        Assert.False(result.Verified); Assert.True(result.PartialStatePossible); Assert.Equal("migration.mail_account_disable_unverified", result.ErrorCode);
        using var quota = new Fixture(); var second = await quota.Prepare(); quota.Provisioner.IgnoreQuota = true;
        var failed = await quota.Target.ApplyAsync(quota.Item, quota.Manifest, quota.Selection, second, default);
        Assert.False(failed.Verified); Assert.Equal("migration.mail_account_settings_unverified", failed.ErrorCode); Assert.Equal(0, quota.Imap.ImportCalls);
    }

    [Fact]
    public async Task Domain_alias_and_mailbox_unknown_states_have_distinct_explicit_decisions()
    {
        using var f = new Fixture(active: null, domainActive: null);
        f.Item.Target.UnknownMailboxActive = true;
        Assert.Contains("migration.mail_domain_state_unknown", (await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        f.Item.Target.UnknownMailDomainActive = false;
        var credentials = await f.Prepare(); var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, credentials, default);
        Assert.True(result.Verified); Assert.False(f.Provisioner.DomainActive); Assert.True(f.Provisioner.Accounts[0].Active);
    }

    [Fact]
    public async Task Known_zero_quota_is_preserved_and_only_the_exact_unknown_account_uses_fallback()
    {
        using var known = new Fixture(quota: 0); known.Item.Target.UnknownMailboxQuotaMb = 4096;
        var result = await known.Target.ApplyAsync(known.Item, known.Manifest, known.Selection, await known.Prepare(), default);
        Assert.True(result.Verified); Assert.Equal(0, known.Provisioner.Accounts[0].QuotaMb);
        using var unknown = new Fixture(quota: 0);
        unknown.Manifest.Issues.Add(new() { Code = "cpanel.mail.quota_unknown", ResourceId = "alice@source.test", Detail = "Source quota not measured." });
        Assert.Contains("migration.mail_quota_unknown", (await unknown.Target.CheckAsync(unknown.Item, unknown.Manifest, unknown.Selection, default)).Blockers);
        unknown.Item.Target.UnknownMailboxQuotaMb = 256;
        var copied = await unknown.Target.ApplyAsync(unknown.Item, unknown.Manifest, unknown.Selection, await unknown.Prepare(), default);
        Assert.True(copied.Verified); Assert.Equal(256, unknown.Provisioner.Accounts[0].QuotaMb);
    }

    [Fact]
    public async Task Local_alias_and_anyone_list_metadata_are_exactly_applied_without_global_helpers()
    {
        using var f = new Fixture();
        f.Domain.Aliases.Add(new() { Address = "info@source.test", Destination = "alice@source.test", Active = false });
        f.Domain.Lists.Add(new() { Address = "team@source.test", Description = "team", PostingPolicy = "anyone", Recipients = ["alice@source.test", "outside@other.test"], Aliases = ["staff@source.test"] });
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, await f.Prepare(), default);
        Assert.True(result.Verified);
        var alias = Assert.Single(f.Provisioner.Aliases); Assert.False(alias.Active); Assert.Equal("alice@target.test", alias.Target);
        var list = Assert.Single(f.Provisioner.Lists); Assert.Equal("team", list.Description);
        Assert.Equal(["alice@target.test", "outside@other.test"], list.Recipients);
        Assert.Equal(["staff@target.test"], list.Aliases);
        Assert.DoesNotContain(f.Provisioner.Mutations, mutation => mutation.Contains("dns") || mutation.Contains("policy") || mutation.Contains("password-change"));
    }

    [Theory]
    [InlineData("forward")]
    [InlineData("policy")]
    public async Task Unsupported_forwarders_and_advanced_list_policy_are_blocked_before_any_mutation(string feature)
    {
        using var f = new Fixture();
        if (feature == "forward") f.Domain.Aliases.Add(new() { Address = "forward@source.test", Destination = "external@elsewhere.test", Active = true });
        else f.Domain.Lists.Add(new() { Address = "list@source.test", PostingPolicy = "subscribers", Recipients = ["alice@source.test"] });
        Assert.NotEmpty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers); Assert.Empty(f.Provisioner.Mutations);
    }

    [Fact]
    public async Task Verification_failure_or_cancellation_retains_partial_resources_and_never_replays()
    {
        using var f = new Fixture(); var credentials = await f.Prepare(); f.Imap.VerifyFailure = true;
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, credentials, default);
        Assert.False(result.Verified); Assert.True(result.PartialStatePossible); Assert.Equal(2, result.CreatedResources.Count);
        Assert.True(f.Provisioner.Exists); Assert.Single(f.Provisioner.Accounts); Assert.Equal(1, f.Imap.ImportCalls);
        var retry = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, credentials, default);
        Assert.False(retry.Verified); Assert.Equal(1, f.Imap.ImportCalls);
        using var cancelled = new Fixture(); var saved = await cancelled.Prepare();
        using var cancellation = new CancellationTokenSource(); cancelled.Imap.OnImport = cancellation.Cancel;
        var partial = await cancelled.Target.ApplyAsync(cancelled.Item, cancelled.Manifest, cancelled.Selection, saved, cancellation.Token);
        Assert.Equal("migration.mail_cancelled", partial.ErrorCode); Assert.True(partial.PartialStatePossible); Assert.Equal(2, partial.CreatedResources.Count); cancelled.AssertClean();
    }

    [Fact]
    public async Task Missing_or_mismatched_durable_credentials_block_before_creation()
    {
        using var f = new Fixture(); await f.Prepare();
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, [], default);
        Assert.False(result.Verified); Assert.Empty(f.Provisioner.Mutations); Assert.False(result.PartialStatePossible);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("metadata")]
    [InlineData("count")]
    [InlineData("hash")]
    public async Task Malformed_or_changed_mail_source_cannot_mutate_target(string failure)
    {
        using var f = new Fixture(corrupt: failure);
        Assert.NotEmpty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        Assert.Empty(f.Provisioner.Mutations); Assert.Equal(0, f.Imap.ImportCalls); f.AssertClean();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("view")]
    [InlineData("unknown-column")]
    public async Task Native_archive_wrong_account_or_unknown_schema_is_rejected_readonly(string corrupt)
    {
        using var f = new Fixture(native: true, corrupt: corrupt);
        Assert.NotEmpty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers); Assert.Empty(f.Provisioner.Mutations);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("cancel")]
    [InlineData("quota")]
    [InlineData("domain-finalize")]
    public async Task Inactive_new_resources_are_restored_even_when_import_cancel_or_finalization_interrupts_temporary_activation(string fault)
    {
        using var f = new Fixture(active: false, domainActive: false); var saved = await f.Prepare(); using var cancellation = new CancellationTokenSource();
        if (fault == "import") f.Imap.VerifyFailure = true;
        if (fault == "cancel") f.Imap.OnImport = cancellation.Cancel;
        if (fault == "quota") f.Provisioner.IgnoreQuota = true;
        if (fault == "domain-finalize") f.Provisioner.FailFirstDomainDisable = true;
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, saved, cancellation.Token);
        Assert.False(result.Verified); Assert.True(result.PartialStatePossible);
        Assert.False(Assert.Single(f.Provisioner.Accounts).Active); Assert.False(f.Provisioner.DomainActive);
        Assert.True(f.Imap.DisabledChecks >= 1); Assert.All(f.Provisioner.DisableTokensCancelled, Assert.False);
        Assert.DoesNotContain(result.RequiredActions, action => action.StartsWith("migration.mail_inactive_state_restore_required"));
        Assert.Contains("migration.mail_inactive_state_restored:alice@target.test", result.Evidence);
    }
    [Fact]
    public async Task Restoration_failure_remains_partial_and_replaced_identities_are_never_modified()
    {
        using var f = new Fixture(active: false, domainActive: false); var saved = await f.Prepare(); f.Provisioner.IgnoreDisable = true; f.Imap.VerifyFailure = true;
        var failed = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, saved, default);
        Assert.False(failed.Verified); Assert.True(failed.PartialStatePossible);
        Assert.Contains("migration.mail_inactive_state_restore_required:alice@target.test", failed.RequiredActions);
        using var replaced = new Fixture(active: false, domainActive: false); saved = await replaced.Prepare(); replaced.Imap.VerifyFailure = true;
        replaced.Imap.OnImport = () => replaced.Provisioner.DomainIdentity = "replacement-domain";
        var result = await replaced.Target.ApplyAsync(replaced.Item, replaced.Manifest, replaced.Selection, saved, default);
        Assert.False(result.Verified); Assert.True(Assert.Single(replaced.Provisioner.Accounts).Active); Assert.True(replaced.Provisioner.DomainActive);
        Assert.Equal([true], replaced.Provisioner.ActiveRequests); Assert.DoesNotContain("domain-active", replaced.Provisioner.Mutations);
        Assert.Contains("migration.mail_inactive_state_restore_required:target.test", result.RequiredActions);
    }
    [Fact]
    public async Task Restoration_does_not_disable_a_replacement_mailbox_and_retry_cannot_replay_import()
    {
        using var f = new Fixture(active: false); var saved = await f.Prepare(); f.Imap.VerifyFailure = true;
        f.Imap.OnImport = () => f.Provisioner.Accounts[0] = f.Provisioner.Accounts[0] with { Id = "replacement-account" };
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, saved, default);
        Assert.True(Assert.Single(f.Provisioner.Accounts).Active); Assert.Equal([true], f.Provisioner.ActiveRequests);
        Assert.Contains("migration.mail_inactive_state_restore_required:alice@target.test", result.RequiredActions);
        Assert.False((await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, saved, default)).Verified);
        Assert.Equal(1, f.Imap.ImportCalls); Assert.Equal([true], f.Provisioner.ActiveRequests);
        Assert.DoesNotContain(saved[0].Password, string.Join(',', result.RequiredActions) + string.Join(',', result.Evidence) + result.ErrorCode);
    }
    [Fact]
    public async Task Unconfirmed_creation_identity_is_not_guessed_but_potentially_active_partial_resource_is_explicit()
    {
        using var f = new Fixture(active: false); var saved = await f.Prepare(); f.Provisioner.LoseMailboxCreateReceipt = true;
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, saved, default);
        Assert.False(result.Verified); Assert.True(result.PartialStatePossible); Assert.Empty(f.Provisioner.ActiveRequests);
        Assert.Contains("migration.mail_inactive_state_restore_required:alice@target.test", result.RequiredActions);
        Assert.Equal(0, f.Imap.ImportCalls);
    }
    [Fact]
    public async Task Quota_unknown_case_insensitive_account_identity_uses_explicit_100_MB_fallback()
    {
        using var f = new Fixture(quota: 0); f.Manifest.Issues.Add(new() { Code = "plesk.mail.quota_unknown", ResourceId = "ALICE@SOURCE.TEST" });
        f.Item.Target.UnknownMailboxQuotaMb = 100;
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, await f.Prepare(), default);
        Assert.True(result.Verified); Assert.Equal(100, Assert.Single(f.Provisioner.Accounts).QuotaMb);
    }
    [Theory]
    [InlineData("INBOX\n", "subscriptions", true, false)]
    [InlineData("Empty\n", "subscriptions", false, true)]
    [InlineData("INBOX.Empty\n", "courierimapsubscribed", false, true)]
    [InlineData("V\t2\n\nEmpty\n", "subscriptions", false, true)]
    [InlineData("", "subscriptions", false, false)]
    public async Task Captured_Dovecot_and_Courier_subscriptions_preserve_subscribed_and_unsubscribed_folders(string content, string name, bool inbox, bool empty)
    {
        using var f = new Fixture(subscriptions: content, subscriptionName: name);
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, await f.Prepare(), default);
        Assert.True(result.Verified); Assert.False(f.Imap.Imported!.SubscriptionStateUnverified);
        Assert.Equal(inbox, f.Imap.Imported.Folders.Single(folder => folder.Path == "INBOX").Subscribed);
        Assert.Equal(empty, f.Imap.Imported.Folders.Single(folder => folder.Path == "Empty").Subscribed);
        Assert.DoesNotContain("migration.mail_folder_subscriptions_unverified", result.RequiredActions);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("not-an-existing-folder\n")]
    [InlineData("V\t9\n\nEmpty\n")]
    public async Task Missing_or_unknown_subscription_provenance_is_explicit_and_does_not_invent_subscribed_state(string? content)
    {
        using var f = new Fixture(subscriptions: content);
        var check = await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default);
        Assert.Contains("migration.mail_folder_subscriptions_unverified", check.Warnings);
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, f.Target.PrepareCredentials(f.Item, f.Manifest), default);
        Assert.Contains("migration.mail_folder_subscriptions_unverified", result.RequiredActions);
        Assert.All(f.Imap.Imported!.Folders, folder => Assert.Null(folder.Subscribed));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_hidden_HTTP_physical_or_application_root_blocks_mail_staging_even_without_configured_public_roots(bool appRoot)
    {
        using var f = new Fixture(); var sites = DispatchProxy.Create<IIisSiteService, SitesProxy>();
        ((SitesProxy)(object)sites).Roots = [new() { PhysicalPath = appRoot ? Path.Combine(f.Root, "elsewhere") : f.Root, ApplicationRootPath = appRoot ? f.Root : "" }];
        var target = f.WithSites(sites);
        Assert.NotEmpty((await target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        Assert.Empty(f.Provisioner.Mutations); Assert.False(Directory.Exists(Path.Combine(f.Root, "migration-mail-targets")));
    }
    public class SitesProxy : DispatchProxy
    {
        public IReadOnlyCollection<SiteDto> Roots = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name == nameof(IIisSiteService.GetMigrationTargetSitesAsync) ? Task.FromResult(Roots) : throw new InvalidOperationException();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "AdaPanel.MailTarget." + Guid.NewGuid().ToString("N"));
        internal readonly Provisioner Provisioner = new();
        internal readonly Imap Imap;
        internal readonly MigrationMailTarget Target;
        internal readonly MigrationPlanRequest Selection = new() { AcceptNewCredentials = true };
        internal readonly MigrationPlanItemDto Item = new() { Id = "item-mail", Kind = "mail", PackageId = Guid.NewGuid(), SourceId = "mail:source.test", Target = new() { TargetName = "target.test" } };
        internal readonly PortableMigrationMailDomain Domain;
        internal readonly PortableMigrationManifest Manifest;
        private readonly Packages packages;
        internal Fixture(bool native = false, bool? active = true, bool? domainActive = true, long quota = 512, string corrupt = "", string? subscriptions = null, string subscriptionName = "subscriptions")
        {
            PortableMigrationStorage.CreatePrivateDirectory(Root);
            var body = Encoding.UTF8.GetBytes("Date: Thu, 1 Jan 1970 00:00:00 +0000\r\nFrom: a@source.test\r\nTo: b@source.test\r\nSubject: body\r\n\r\nreal message body");
            var archive = native ? Native(body, corrupt) : Maildir(body, corrupt, subscriptions, subscriptionName);
            Domain = new() { Id = Item.SourceId, Name = "source.test", Active = domainActive, Accounts = [new() { Email = "alice@source.test", Active = active, QuotaMb = quota, EmailCount = corrupt == "count" ? 2 : 1, ArchivePath = "mail/alice", ArchiveFormat = native ? "vandelay-sqlite" : "maildir-zip" }] };
            Manifest = new() { PackageId = Item.PackageId, Provider = native ? PortableMigrationProvider.Adapanel : PortableMigrationProvider.Cpanel, CreatedAtUtc = DateTimeOffset.UtcNow, SourceName = "source", Complete = true,
                MailDomains = [Domain], Payloads = [new() { Path = "mail/alice", Size = archive.Length, Sha256 = corrupt == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(archive)) }] };
            packages = new Packages(archive); Imap = new(Provisioner);
            Target = new(Provisioner, packages, new Gates(), new PanelRuntimeOptions { DataRoot = Root }, new(), new(), Imap);
        }
        internal MigrationMailTarget WithSites(IIisSiteService sites) => new(Provisioner, packages, new Gates(), new PanelRuntimeOptions { DataRoot = Root }, new(), new(), Imap, sites);
        internal async Task<IReadOnlyList<MigrationCreatedCredential>> Prepare()
        { Assert.Empty((await Target.CheckAsync(Item, Manifest, Selection, default)).Blockers); return Target.PrepareCredentials(Item, Manifest); }
        private byte[] Native(byte[] body, string corrupt)
        {
            var path = Path.Combine(Root, "fixture.sqlite");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
            {
                db.Open(); using var schema = db.CreateCommand(); schema.CommandText = """
                    CREATE TABLE sources(id INTEGER PRIMARY KEY,kind TEXT,session_url TEXT,account_id TEXT,account_name TEXT,username TEXT);
                    CREATE TABLE blobs(id INTEGER PRIMARY KEY,hash BLOB,data BLOB);
                    CREATE TABLE mailboxes(id INTEGER PRIMARY KEY,name TEXT,parent_id INTEGER,role TEXT,sort_order INTEGER,is_subscribed INTEGER);
                    CREATE TABLE emails(id INTEGER PRIMARY KEY,blob_id INTEGER,received_at TEXT,mailbox_ids TEXT,keywords TEXT,message_match TEXT);
                    INSERT INTO sources VALUES(1,'jmap','https://source.test','one','alice@source.test','admin');
                    INSERT INTO mailboxes VALUES(1,'Inbox',NULL,'inbox',0,1),(2,'Archive',NULL,NULL,1,1);
                    INSERT INTO emails VALUES(1,1,'2022-02-03T04:05:06Z','[1,2]','["$seen","$answered"]','{}');
                    """; schema.ExecuteNonQuery();
                using var blob = db.CreateCommand(); blob.CommandText = "INSERT INTO blobs VALUES(1,@hash,@body)"; blob.Parameters.AddWithValue("@hash", SHA256.HashData(body)); blob.Parameters.AddWithValue("@body", body); blob.ExecuteNonQuery();
                if (corrupt.Length > 0)
                {
                    using var change = db.CreateCommand(); change.CommandText = corrupt switch
                    { "owner" => "UPDATE sources SET account_name='foreign@other.test'", "view" => "DROP TABLE emails; CREATE VIEW emails AS SELECT randomblob(1000000000)", "unknown-column" => "ALTER TABLE emails ADD COLUMN unexpected TEXT", _ => "SELECT 1" }; change.ExecuteNonQuery();
                }
            }
            return File.ReadAllBytes(path);
        }
        private static byte[] Maildir(byte[] body, string corrupt, string? subscriptions, string subscriptionName)
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var directory in new[] { "cur", "new", "tmp", ".Empty/cur", ".Empty/new", ".Empty/tmp" }) zip.CreateEntry(directory + "/");
                using (var content = zip.CreateEntry("cur/17123:2,RS").Open()) content.Write(body);
                if (subscriptions != null) { using var stream = zip.CreateEntry(subscriptionName).Open(); stream.Write(Encoding.UTF8.GetBytes(subscriptions)); }
                if (corrupt != "metadata")
                {
                    using var metadata = zip.CreateEntry(PortableMaildirWriter.MetadataEntryName).Open();
                    var entries = new List<object> { new { path = "cur/17123:2,RS", sourceDateUtc = corrupt == "date" ? "invalid" : SourceDate.ToString("O"), unixMode = 384, maildirFlags = "RS", isMessage = true } };
                    if (subscriptions != null) entries.Add(new { path = subscriptionName, sourceDateUtc = SourceDate.ToString("O"), unixMode = 384, maildirFlags = "", isMessage = false });
                    JsonSerializer.Serialize(metadata, new { version = 1, format = "maildir-source-metadata", files = entries });
                }
            }
            return bytes.ToArray();
        }
        internal void AssertClean() { var stage = Path.Combine(Root, "migration-mail-targets"); if (Directory.Exists(stage)) Assert.Empty(Directory.GetFileSystemEntries(stage)); }
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
    private sealed class Provisioner : IMigrationMailProvisioner
    {
        internal bool Exists, DomainActive = true, IgnoreQuota, IgnoreDisable;
        internal bool FailFirstDomainDisable, LoseMailboxCreateReceipt;
        internal string DomainIdentity = "domain-id";
        internal List<bool> DisableTokensCancelled = [];
        internal string Missing = "";
        internal List<string> Mutations = [];
        internal List<bool> ActiveRequests = [];
        internal List<MigrationMailAccountState> Accounts = [];
        internal List<MailAliasDto> Aliases = [];
        internal List<MailListDto> Lists = [];
        public Task<MigrationMailSnapshot> ReadAsync(string domain, CancellationToken token) => Task.FromResult(new MigrationMailSnapshot
        { Ready = Missing != "ready", SchemaSupported = Missing != "schema", DomainExists = Exists, DomainId = Exists ? DomainIdentity : "", DomainActive = Exists ? DomainActive : null, ManualManagement = true,
            Endpoint = Missing == "endpoint" ? null : new("mail.target.test", 993, false), Accounts = Accounts.ToArray(), Aliases = Aliases.ToArray(), Lists = Lists.ToArray() });
        public Task<string> CreateDomainFreshAsync(string domain, CancellationToken token) { Mutations.Add("create-domain"); if (Exists) throw new AppException("migration.target_mail_domain_exists"); Exists = true; return Task.FromResult("domain-id"); }
        public Task<string> CreateMailboxFreshAsync(string domainId, string address, string password, CancellationToken token)
        { Mutations.Add("create-mailbox"); Accounts.Add(new("account-id", address, null, true)); if (LoseMailboxCreateReceipt) throw new IOException(); return Task.FromResult("account-id"); }
        public Task SetMailboxQuotaAsync(string id, string address, long quota, CancellationToken token)
        { Mutations.Add("quota"); Accounts[0] = Accounts[0] with { QuotaMb = IgnoreQuota ? quota + 1 : quota }; return Task.CompletedTask; }
        public Task SetMailboxActiveAsync(string id, string address, bool active, CancellationToken token)
        { if (!active) DisableTokensCancelled.Add(token.IsCancellationRequested); ActiveRequests.Add(active); Mutations.Add("active"); if (active || !IgnoreDisable) Accounts[0] = Accounts[0] with { Active = active }; return Task.CompletedTask; }
        public Task SetDomainActiveAsync(string id, string domain, bool active, CancellationToken token) { Mutations.Add("domain-active"); if (!active && FailFirstDomainDisable) { FailFirstDomainDisable = false; throw new IOException(); } DomainActive = active; return Task.CompletedTask; }
        public Task CreateAliasFreshAsync(string domainId, string accountId, string address, bool active, CancellationToken token)
        { Mutations.Add("alias"); Aliases.Add(new() { Address = address, Target = Accounts.Single(account => account.Id == accountId).Address, Active = active }); return Task.CompletedTask; }
        public Task<string> CreateListFreshAsync(string domainId, CreateMailListRequest request, IReadOnlyCollection<string> aliases, CancellationToken token)
        { Mutations.Add("list"); Lists.Add(new() { Address = request.Address, Description = request.Description, Recipients = request.Recipients, Aliases = aliases, PostingPolicy = request.PostingPolicy }); return Task.FromResult("list-id"); }
    }
    private sealed class Imap(Provisioner target) : IMigrationMailImap
    {
        internal int ImportCalls, DisabledChecks;
        internal MigrationMailDataset? Imported;
        internal bool VerifyFailure;
        internal Action? OnImport;
        public Task ProbeAsync(MigrationMailEndpoint endpoint, CancellationToken token) => Task.CompletedTask;
        public Task<IMigrationMailboxSession> OpenAsync(MigrationMailEndpoint endpoint, string address, string password, string stage, CancellationToken token)
        { Assert.True(target.Accounts.Single(account => account.Address == address).Active); return Task.FromResult<IMigrationMailboxSession>(new Session(this)); }
        public Task VerifyDisabledAsync(MigrationMailEndpoint endpoint, string address, string password, CancellationToken token)
        { DisabledChecks++; if (target.Accounts.Single(account => account.Address == address).Active != false) throw new AppException("migration.mail_account_disable_unverified"); return Task.CompletedTask; }
        private sealed class Session(Imap owner) : IMigrationMailboxSession
        {
            public Task ImportAndVerifyAsync(MigrationMailDataset source, CancellationToken token)
            { owner.ImportCalls++; owner.Imported = source; owner.OnImport?.Invoke(); token.ThrowIfCancellationRequested(); if (owner.VerifyFailure) throw new IOException("DO-NOT-EXPOSE-PASSWORD"); return Task.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Packages(byte[] archive) : IMigrationPackageStore
    {
        public Task<Stream> OpenPayloadAsync(Guid id, string path, CancellationToken token = default) => Task.FromResult<Stream>(new MemoryStream(archive));
        public Task<PortableMigrationPackage> UploadAsync(Stream input, CancellationToken token = default) => throw new NotSupportedException();
        public Task<PortableMigrationManifest> ReadManifestAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class Gates : IConcurrencyLock
    {
        public Task RunExclusiveAsync(Func<Task> action, CancellationToken token = default) => action();
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken token = default) => action();
        public Task RunExclusiveAsync(string key, Func<Task> action, CancellationToken token = default) => action();
        public Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> action, CancellationToken token = default) => action();
    }
}
