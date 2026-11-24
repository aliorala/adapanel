using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed class MigrationMailTarget : IMigrationTargetAdapter
{
    private readonly IMigrationMailProvisioner provisioner;
    private readonly IMigrationPackageStore packages;
    private readonly IConcurrencyLock gates;
    private readonly PanelRuntimeOptions runtime;
    private readonly PortableMigrationPackageOptions limits;
    private readonly MigrationMailTargetOptions mailLimits;
    private readonly IMigrationMailImap imap;
    private readonly IIisSiteService? sites;
    private readonly ConcurrentDictionary<string, MigrationMailEndpoint> checkedEndpoints = new(StringComparer.Ordinal);
    public string Kind => "mail";
    public MigrationMailTarget(IMigrationMailProvisioner provisioner, IMigrationPackageStore packages, IConcurrencyLock gates,
        IOptions<PanelRuntimeOptions> runtime, IOptions<PortableMigrationPackageOptions> limits, IOptions<MigrationMailTargetOptions> mailLimits, IIisSiteService? sites = null)
        : this(provisioner, packages, gates, runtime.Value, limits.Value, mailLimits.Value, new MigrationMailImap(), sites) { }
    internal MigrationMailTarget(IMigrationMailProvisioner provisioner, IMigrationPackageStore packages, IConcurrencyLock gates,
        PanelRuntimeOptions runtime, PortableMigrationPackageOptions limits, MigrationMailTargetOptions mailLimits, IMigrationMailImap imap, IIisSiteService? sites = null)
    { this.provisioner = provisioner; this.packages = packages; this.gates = gates; this.runtime = runtime; this.limits = limits; this.mailLimits = mailLimits; this.imap = imap; this.sites = sites; }

    public async Task<MigrationTargetCheck> CheckAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest,
        MigrationPlanRequest selection, CancellationToken cancellationToken)
    {
        var result = new MigrationTargetCheck(); string? stage = null;
        checkedEndpoints.TryRemove(item.Id, out _);
        try
        {
            var source = Source(item, manifest); var domain = Domain(item.Target.TargetName);
            MetadataChecks(item, manifest, source, selection, result);
            var snapshot = await provisioner.ReadAsync(domain, cancellationToken);
            StateChecks(snapshot, source, item, result);
            if (result.Blockers.Count > 0) return result;
            if (source.Accounts.Count > 0)
            {
                await imap.ProbeAsync(snapshot.Endpoint!, cancellationToken);
                stage = await StageAsync(cancellationToken);
                var reader = new MigrationMailArchiveReader(packages, limits, mailLimits, stage);
                foreach (var account in source.Accounts)
                {
                    var data = await reader.ReadAsync(item.PackageId, account, manifest, cancellationToken);
                    if (data.HasNonMailData) result.Warnings.Add("migration.mail_native_non_message_data_requires_separate_restore");
                    if (data.HasSubsecondDates) result.Warnings.Add("migration.mail_imap_dates_have_second_precision");
                    if (data.SubscriptionStateUnverified) result.Warnings.Add("migration.mail_folder_subscriptions_unverified");
                    if (account.EmailCount == null) result.Warnings.Add("migration.mail_source_capture_count_unmeasured");
                }
            }
            if (source.Accounts.Count > 0) checkedEndpoints[item.Id] = snapshot.Endpoint!;
            result.Warnings.Add("migration.mail_dns_dkim_smtp_cutover_required");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { result.Blockers.Add(Safe(error, "migration.mail_preflight_failed")); }
        finally { if (stage != null && Directory.Exists(stage)) PortableMigrationStorage.DeleteOwnedDirectory(stage); }
        return result;
    }

    public IReadOnlyList<MigrationCreatedCredential> PrepareCredentials(MigrationPlanItemDto item, PortableMigrationManifest manifest)
    {
        var source = Source(item, manifest); var domain = Domain(item.Target.TargetName);
        if (source.Accounts.Count == 0) return [];
        if (!checkedEndpoints.TryGetValue(item.Id, out var endpoint)) throw Error("preflight_required");
        return source.Accounts.Select(account =>
        {
            var address = Rewrite(account.Email, source.Name, domain);
            return new MigrationCreatedCredential { ItemId = item.Id, Kind = "mail", ResourceName = address, UserName = address,
                Password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)) + "!aA1", Host = endpoint.Host };
        }).ToArray();
    }

    public async Task<MigrationComponentResult> ApplyAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest,
        MigrationPlanRequest selection, IReadOnlyList<MigrationCreatedCredential> credentials, CancellationToken cancellationToken)
    {
        var result = new MigrationComponentResult();
        try { return await gates.RunExclusiveAsync("migration-mail:" + Domain(item.Target.TargetName), ApplyCoreAsync, cancellationToken); }
        catch (OperationCanceledException) { result.ErrorCode = "migration.mail_cancelled"; return result; }
        async Task<MigrationComponentResult> ApplyCoreAsync()
        {
            string? stage = null;
            string? ownedDomain = null, ownedDomainId = null;
            bool restoreDomainDisabled = false;
            MigrationMailEndpoint? ownedEndpoint = null;
            var restoreAccounts = new List<(string Id, string Address, string Password)>();
            var unconfirmedInactiveResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var source = Source(item, manifest); var domain = Domain(item.Target.TargetName);
                var check = new MigrationTargetCheck(); MetadataChecks(item, manifest, source, selection, check);
                var snapshot = await provisioner.ReadAsync(domain, cancellationToken); StateChecks(snapshot, source, item, check);
                if (check.Blockers.Count > 0) { result.ErrorCode = check.Blockers[0]; return result; }
                var prepared = new Dictionary<string, MigrationCreatedCredential>(StringComparer.OrdinalIgnoreCase);
                foreach (var account in source.Accounts)
                {
                    var address = Rewrite(account.Email, source.Name, domain);
                    var credential = credentials.SingleOrDefault(credential => credential.ItemId == item.Id && credential.Kind == "mail" && credential.ResourceName.Equals(address, StringComparison.OrdinalIgnoreCase));
                    if (credential == null || credential.Password.Length < 32 || credential.UserName != address || credential.Host != snapshot.Endpoint!.Host) throw Error("durable_credentials_required");
                    prepared.Add(address, credential);
                }
                if (credentials.Count != source.Accounts.Count) throw Error("durable_credentials_required");
                stage = await StageAsync(cancellationToken); var reader = new MigrationMailArchiveReader(packages, limits, mailLimits, stage);
                var data = new Dictionary<string, MigrationMailDataset>(StringComparer.OrdinalIgnoreCase);
                foreach (var account in source.Accounts)
                {
                    var captured = await reader.ReadAsync(item.PackageId, account, manifest, cancellationToken);
                    data.Add(account.Email, captured);
                    if (captured.HasNonMailData) result.RequiredActions.Add("migration.mail_native_non_message_data_requires_separate_restore");
                    if (captured.HasSubsecondDates) result.RequiredActions.Add("migration.mail_imap_dates_have_second_precision");
                    if (captured.SubscriptionStateUnverified) result.RequiredActions.Add("migration.mail_folder_subscriptions_unverified");
                    if (account.EmailCount == null) result.RequiredActions.Add("migration.mail_source_capture_count_unmeasured");
                }
                // Long source reads do not reserve a target. Reread under the mutation lease before CREATE.
                var originalEndpoint = snapshot.Endpoint;
                snapshot = await provisioner.ReadAsync(domain, cancellationToken); check = new(); StateChecks(snapshot, source, item, check);
                if (check.Blockers.Count > 0) { result.ErrorCode = check.Blockers[0]; return result; }
                if (snapshot.Endpoint != originalEndpoint) throw Error("imap_identity_changed");
                cancellationToken.ThrowIfCancellationRequested();
                result.PartialStatePossible = true;
                result.Evidence.Add("attempted:mail-domain:" + domain);
                if (!(source.Active ?? item.Target.UnknownMailDomainActive!.Value)) unconfirmedInactiveResources.Add(domain);
                var domainId = await provisioner.CreateDomainFreshAsync(domain, cancellationToken);
                ownedDomain = domain; ownedDomainId = domainId; ownedEndpoint = snapshot.Endpoint;
                unconfirmedInactiveResources.Remove(domain);
                restoreDomainDisabled = !(source.Active ?? item.Target.UnknownMailDomainActive!.Value);
                result.CreatedResources.Add("mail-domain:" + domain);
                var created = await provisioner.ReadAsync(domain, cancellationToken);
                if (!created.DomainExists || created.DomainId != domainId || created.DomainActive != true || !created.ManualManagement) throw Error("domain_creation_unverified");
                var accountIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var account in source.Accounts)
                {
                    var address = Rewrite(account.Email, source.Name, domain); var secret = prepared[address];
                    result.Evidence.Add("attempted:mailbox:" + address);
                    if (!(account.Active ?? item.Target.UnknownMailboxActive!.Value)) unconfirmedInactiveResources.Add(address);
                    var accountId = await provisioner.CreateMailboxFreshAsync(domainId, address, secret.Password, cancellationToken);
                    if (!(account.Active ?? item.Target.UnknownMailboxActive!.Value)) restoreAccounts.Add((accountId, address, secret.Password));
                    unconfirmedInactiveResources.Remove(address);
                    accountIds.Add(address, accountId); result.CreatedResources.Add("mailbox:" + address);
                    await provisioner.SetMailboxQuotaAsync(accountId, address, Quota(account, manifest, item), cancellationToken);
                    await provisioner.SetMailboxActiveAsync(accountId, address, true, cancellationToken);
                    var beforeImport = await provisioner.ReadAsync(domain, cancellationToken);
                    var measured = beforeImport.Accounts.SingleOrDefault(box => box.Id == accountId && box.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
                    if (measured?.Active != true || measured.QuotaMb != Quota(account, manifest, item)) throw Error("account_settings_unverified");
                    await using (var session = await imap.OpenAsync(snapshot.Endpoint!, address, secret.Password, stage, cancellationToken))
                        await session.ImportAndVerifyAsync(data[account.Email], cancellationToken);
                    result.Evidence.Add("migration.mail_message_hash_folder_flags_utc_seconds_verified:" + address + ":" + data[account.Email].Messages.Count);
                    var active = account.Active ?? item.Target.UnknownMailboxActive!.Value;
                    if (!active)
                    {
                        await provisioner.SetMailboxActiveAsync(accountId, address, false, cancellationToken);
                        await imap.VerifyDisabledAsync(snapshot.Endpoint!, address, secret.Password, cancellationToken);
                    }
                }
                foreach (var alias in source.Aliases)
                {
                    var address = Rewrite(alias.Address, source.Name, domain); var destination = Rewrite(alias.Destination, source.Name, domain);
                    await provisioner.CreateAliasFreshAsync(domainId, accountIds[destination], address, alias.Active ?? item.Target.UnknownMailAliasActive!.Value, cancellationToken);
                    result.CreatedResources.Add("mail-alias:" + address);
                }
                foreach (var list in source.Lists)
                {
                    var address = Rewrite(list.Address, source.Name, domain);
                    await provisioner.CreateListFreshAsync(domainId, new()
                    {
                        Domain = domain, Address = address, Description = list.Description, PostingPolicy = list.PostingPolicy,
                        Recipients = list.Recipients.Select(recipient => RewriteIfLocal(recipient, source.Name, domain)).ToArray(), AllowedSenders = []
                    }, list.Aliases.Select(alias => Rewrite(alias, source.Name, domain)).ToArray(), cancellationToken);
                    result.CreatedResources.Add("mail-list:" + address);
                }
                var domainActive = source.Active ?? item.Target.UnknownMailDomainActive!.Value;
                await provisioner.SetDomainActiveAsync(domainId, domain, domainActive, cancellationToken);
                var final = await provisioner.ReadAsync(domain, cancellationToken);
                if (final.DomainId != domainId || final.DomainActive != domainActive || !final.ManualManagement || final.Accounts.Count != source.Accounts.Count || final.Aliases.Count != source.Aliases.Count || final.Lists.Count != source.Lists.Count) throw Error("final_inventory_unverified");
                foreach (var account in source.Accounts)
                {
                    var address = Rewrite(account.Email, source.Name, domain);
                    var actual = final.Accounts.SingleOrDefault(box => box.Id == accountIds[address] && box.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
                    if (actual?.QuotaMb != Quota(account, manifest, item) || actual.Active != (account.Active ?? item.Target.UnknownMailboxActive!.Value)) throw Error("account_settings_unverified");
                }
                foreach (var alias in source.Aliases)
                    if (!final.Aliases.Any(actual => actual.Address.Equals(Rewrite(alias.Address, source.Name, domain), StringComparison.OrdinalIgnoreCase)
                        && actual.Target.Equals(Rewrite(alias.Destination, source.Name, domain), StringComparison.OrdinalIgnoreCase) && actual.Active == (alias.Active ?? item.Target.UnknownMailAliasActive!.Value))) throw Error("alias_unverified");
                foreach (var list in source.Lists)
                {
                    var actual = final.Lists.SingleOrDefault(actual => actual.Address.Equals(Rewrite(list.Address, source.Name, domain), StringComparison.OrdinalIgnoreCase));
                    if (actual == null || actual.Description != list.Description || actual.PostingPolicy != list.PostingPolicy || actual.AllowedSenders.Count != 0 ||
                        !Equal(actual.Recipients, list.Recipients.Select(recipient => RewriteIfLocal(recipient, source.Name, domain))) ||
                        !Equal(actual.Aliases, list.Aliases.Select(alias => Rewrite(alias, source.Name, domain)))) throw Error("list_unverified");
                }
                cancellationToken.ThrowIfCancellationRequested();
                result.RequiredActions.Add("migration.mail_dns_dkim_smtp_cutover_required");
                result.Verified = true; result.PartialStatePossible = false; return result;
            }
            catch (OperationCanceledException) { result.ErrorCode = "migration.mail_cancelled"; return result; }
            catch (Exception error) { result.ErrorCode = Safe(error, "migration.mail_import_failed"); return result; }
            finally
            {
                foreach (var unconfirmed in unconfirmedInactiveResources)
                    result.RequiredActions.Add("migration.mail_inactive_state_restore_required:" + unconfirmed);
                // Only CREATE-confirmed identities belong to this attempt. This guard runs even when
                // import, quota, readback or the caller's cancellation interrupts temporary activation.
                if (ownedDomain != null && ownedDomainId != null && (restoreAccounts.Count > 0 || restoreDomainDisabled))
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    async Task RestoreAsync(string resource, Func<Task> action)
                    {
                        try { await action(); result.Evidence.Add("migration.mail_inactive_state_restored:" + resource); }
                        catch
                        {
                            result.Verified = false; result.PartialStatePossible = true;
                            if (string.IsNullOrEmpty(result.ErrorCode)) result.ErrorCode = "migration.mail_inactive_state_restore_unverified";
                            result.RequiredActions.Add("migration.mail_inactive_state_restore_required:" + resource);
                        }
                    }
                    foreach (var account in restoreAccounts)
                        await RestoreAsync(account.Address, async () =>
                        {
                            var current = await provisioner.ReadAsync(ownedDomain, cleanup.Token);
                            if (!current.Ready || !current.SchemaSupported || !current.DomainExists || current.DomainId != ownedDomainId || !current.Accounts.Any(box => box.Id == account.Id && box.Address.Equals(account.Address, StringComparison.OrdinalIgnoreCase))) throw Error("identity_changed");
                            await provisioner.SetMailboxActiveAsync(account.Id, account.Address, false, cleanup.Token);
                            current = await provisioner.ReadAsync(ownedDomain, cleanup.Token);
                            if (!current.Ready || !current.SchemaSupported || !current.DomainExists || current.DomainId != ownedDomainId || !current.Accounts.Any(box => box.Id == account.Id && box.Address.Equals(account.Address, StringComparison.OrdinalIgnoreCase) && box.Active == false)) throw Error("account_disable_unverified");
                            if (current.Endpoint != ownedEndpoint || ownedEndpoint == null) throw Error("imap_identity_changed");
                            await imap.VerifyDisabledAsync(ownedEndpoint, account.Address, account.Password, cleanup.Token);
                        });
                    if (restoreDomainDisabled)
                        await RestoreAsync(ownedDomain, async () =>
                        {
                            var current = await provisioner.ReadAsync(ownedDomain, cleanup.Token);
                            if (!current.Ready || !current.SchemaSupported || !current.DomainExists || current.DomainId != ownedDomainId) throw Error("identity_changed");
                            await provisioner.SetDomainActiveAsync(ownedDomainId, ownedDomain, false, cleanup.Token);
                            current = await provisioner.ReadAsync(ownedDomain, cleanup.Token);
                            if (!current.Ready || !current.SchemaSupported || !current.DomainExists || current.DomainId != ownedDomainId || current.DomainActive != false) throw Error("domain_disable_unverified");
                        });
                }
                if (stage != null && Directory.Exists(stage))
                    try { PortableMigrationStorage.DeleteOwnedDirectory(stage); }
                    catch
                    {
                        result.Verified = false;
                        if (string.IsNullOrEmpty(result.ErrorCode)) result.ErrorCode = "migration.mail_private_stage_cleanup_failed";
                        result.RequiredActions.Add("migration.mail_private_stage_cleanup_required");
                    }
            }
        }
    }
    private static void MetadataChecks(MigrationPlanItemDto item, PortableMigrationManifest manifest, PortableMigrationMailDomain source, MigrationPlanRequest selection, MigrationTargetCheck check)
    {
        Domain(source.Name); var target = Domain(item.Target.TargetName);
        if (source.Accounts.Count > 0 && !selection.AcceptNewCredentials) check.Blockers.Add("migration.new_credentials_acceptance_required");
        if (!source.Active.HasValue && !item.Target.UnknownMailDomainActive.HasValue) check.Blockers.Add("migration.mail_domain_state_unknown");
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in source.Accounts)
        {
            if (!occupied.Add(Rewrite(account.Email, source.Name, target))) check.Blockers.Add("migration.mail_source_address_collision");
            if (!account.Active.HasValue && !item.Target.UnknownMailboxActive.HasValue) check.Blockers.Add("migration.mail_account_state_unknown");
            if (QuotaUnknown(account, manifest) && !item.Target.UnknownMailboxQuotaMb.HasValue) check.Blockers.Add("migration.mail_quota_unknown");
            if (Quota(account, manifest, item) is < 0 or > 1_048_576) check.Blockers.Add("migration.mail_quota_invalid");
            if (account.ArchiveFormat is not ("maildir-zip" or "vandelay-sqlite") || account.ArchivePath.Length == 0) check.Blockers.Add("migration.mail_archive_unsupported");
        }
        foreach (var alias in source.Aliases)
        {
            if (!occupied.Add(Rewrite(alias.Address, source.Name, target))) check.Blockers.Add("migration.mail_source_address_collision");
            if (!source.Accounts.Any(account => account.Email.Equals(alias.Destination, StringComparison.OrdinalIgnoreCase))) check.Blockers.Add("migration.mail_forwarder_unsupported");
            if (!alias.Active.HasValue && !item.Target.UnknownMailAliasActive.HasValue) check.Blockers.Add("migration.mail_alias_state_unknown");
        }
        foreach (var list in source.Lists)
        {
            if (!occupied.Add(Rewrite(list.Address, source.Name, target))) check.Blockers.Add("migration.mail_source_address_collision");
            foreach (var alias in list.Aliases) if (!occupied.Add(Rewrite(alias, source.Name, target))) check.Blockers.Add("migration.mail_source_address_collision");
            if (list.PostingPolicy != MailListPostingPolicy.Anyone || list.AllowedSenders.Count != 0) check.Blockers.Add("migration.mail_list_policy_unsupported");
            foreach (var recipient in list.Recipients) Address(recipient);
        }
    }
    private static void StateChecks(MigrationMailSnapshot snapshot, PortableMigrationMailDomain source, MigrationPlanItemDto item, MigrationTargetCheck check)
    {
        if (!snapshot.Ready) check.Blockers.Add("migration.mail_target_not_ready");
        if (!snapshot.SchemaSupported) check.Blockers.Add("migration.mail_schema_unsupported");
        check.Blockers.AddRange(snapshot.Blockers);
        if (snapshot.DomainExists) check.Blockers.Add("migration.target_mail_domain_exists");
        if (snapshot.Accounts.Count + snapshot.Aliases.Count + snapshot.Lists.Count > 0) check.Blockers.Add("migration.target_mail_resources_exist");
        if (source.Accounts.Count > 0 && snapshot.Endpoint == null) check.Blockers.Add("migration.mail_imap_endpoint_unmeasured");
    }
    private async Task<string> StageAsync(CancellationToken token)
    {
        var publicRoots = await MigrationStoragePolicy.PublicRootsAsync(sites, limits.PublicWebRoots, token);
        var parent = Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(runtime.DataRoot), "migration-mail-targets"));
        PortableMigrationStorage.CreatePrivateDirectory(parent, publicRoots);
        var stage = Path.Combine(parent, Guid.NewGuid().ToString("N")); PortableMigrationStorage.CreatePrivateDirectory(stage, publicRoots); return stage;
    }
    private static PortableMigrationMailDomain Source(MigrationPlanItemDto item, PortableMigrationManifest manifest) => manifest.MailDomains.Single(domain => domain.Id == item.SourceId);
    private static bool QuotaUnknown(PortableMigrationMailAccount account, PortableMigrationManifest manifest) => manifest.Issues.Any(issue => string.Equals(issue.ResourceId, account.Email, StringComparison.OrdinalIgnoreCase) && issue.Code is "cpanel.mail.quota_unknown" or "plesk.mail.quota_unknown");
    private static long Quota(PortableMigrationMailAccount account, PortableMigrationManifest manifest, MigrationPlanItemDto item) => QuotaUnknown(account, manifest) ? item.Target.UnknownMailboxQuotaMb ?? -1 : account.QuotaMb;
    private static string RewriteIfLocal(string address, string source, string target) { address = Address(address); return address.EndsWith("@" + source, StringComparison.OrdinalIgnoreCase) ? Rewrite(address, source, target) : address; }
    private static string Rewrite(string address, string source, string target)
    { address = Address(address); if (!address.EndsWith("@" + source, StringComparison.OrdinalIgnoreCase)) throw Error("source_address_scope_invalid"); return address[..address.LastIndexOf('@')] + "@" + target; }
    private static string Address(string address)
    { if (!Regex.IsMatch(address, @"\A[A-Za-z0-9][A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]{0,63}@[A-Za-z0-9.-]+\z", RegexOptions.CultureInvariant)) throw Error("address_invalid"); Domain(address[(address.LastIndexOf('@') + 1)..]); return address.ToLowerInvariant(); }
    private static string Domain(string domain)
    { domain = domain.Trim().ToLowerInvariant(); if (domain.Length is 0 or > 253 || !domain.Contains('.') || domain.Split('.').Any(part => part.Length is 0 or > 63 || !Regex.IsMatch(part, @"\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\z", RegexOptions.CultureInvariant))) throw Error("domain_invalid"); return domain; }
    private static bool Equal(IEnumerable<string> left, IEnumerable<string> right) => left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);
    private static string Safe(Exception error, string fallback) => error is AppException app && app.MessageKey.StartsWith("migration.", StringComparison.Ordinal) ? app.MessageKey : fallback;
    private static AppException Error(string code) => new("migration.mail_" + code, 409, 409);
}
