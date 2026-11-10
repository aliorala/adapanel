using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailService : IMigrationMailProvisioner
{
    public async Task<MigrationMailSnapshot> ReadAsync(string domain, CancellationToken cancellationToken)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var component = await dbContext.InstalledComponents.AsNoTracking().SingleOrDefaultAsync(component => component.ComponentId == "stalwart-mail", cancellationToken);
        var version = component?.Version.TrimStart('v') ?? "";
        // The management payload is verified against this exact official registry schema. Unknown
        // releases are not guessed from similar field names or a successful generic CLI exit.
        // Official v0.16.26 has the same used fields/enums as v0.16.9; only unrelated
        // Domain.allowScimProvisioning and UserAccount.externalId were added.
        // https://github.com/stalwartlabs/stalwart/blob/v0.16.26/crates/registry/src/schema/structs.rs
        if (version is not ("0.16.9" or "0.16.26")) return new() { Ready = true, Version = version, Blockers = ["migration.mail_schema_unsupported"] };
        foreach (var (type, fields) in new[]
        {
            ("Account/User", new[] { "name", "domainId", "credentials", "permissions", "quotas", "aliases" }),
            ("Domain", new[] { "isEnabled", "certificateManagement", "dkimManagement", "dnsManagement" }),
            ("MailingList", new[] { "recipients", "aliases", "domainId" })
        })
        {
            var description = await RunCliAsync(cli, "describe " + type, env, cancellationToken);
            if (!description.Success || fields.Any(field => !description.StandardOutput.Contains(field, StringComparison.Ordinal)))
                return new() { Ready = true, Version = version, Blockers = ["migration.mail_schema_unsupported"] };
        }
        var domains = await MigrationQueryAsync(cli, "domain", env, cancellationToken);
        var accounts = await MigrationQueryAsync(cli, "account", env, cancellationToken);
        var lists = await MigrationQueryAsync(cli, MailingListObject, env, cancellationToken);
        var row = domains.SingleOrDefault(item => GetField(item, "name").Equals(domain, StringComparison.OrdinalIgnoreCase));
        var domainId = row.ValueKind == JsonValueKind.Object ? GetField(row, "id") : "";
        JsonElement? detail = domainId.Length == 0 ? null : await GetObjectAsync(cli, "domain", domainId, env, cancellationToken);
        if (domainId.Length > 0 && detail == null) throw MigrationMailError("lookup_unmeasured");
        var results = new List<MigrationMailAccountState>();
        var aliases = new List<MailAliasDto>();
        var resultLists = new List<MailListDto>();
        foreach (var account in accounts)
        {
            if (domainId.Length == 0 || GetField(account, "domainId") != domainId && !BelongsToDomain(GetField(account, "emailAddress"), domain)) continue;
            var id = GetField(account, "id");
            var current = await GetObjectAsync(cli, "account", id, env, cancellationToken) ?? throw MigrationMailError("lookup_unmeasured");
            if (!IsMailboxRecord(account, current).GetValueOrDefault()) throw MigrationMailError("account_type_unsupported");
            var address = GetField(current, "emailAddress");
            if (address.Length == 0) address = GetField(current, "name") + "@" + domain;
            long? quota = null;
            if (current.TryGetProperty("quotas", out var quotas) && quotas.ValueKind == JsonValueKind.Object && quotas.TryGetProperty("maxDiskQuota", out var bytes) && bytes.TryGetInt64(out var measured) && measured >= 0)
                quota = measured / (1024 * 1024);
            results.Add(new(id, address, quota, MigrationAccountActive(current)));
            if (current.TryGetProperty("aliases", out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var alias in map.EnumerateObject().Select(item => item.Value).Where(item => GetField(item, "domainId") == domainId))
                    aliases.Add(new() { Address = GetField(alias, "name") + "@" + domain, Target = address, Active = OkunanEtkinlik(alias) });
        }
        foreach (var list in lists)
        {
            if (domainId.Length == 0 || GetField(list, "domainId") != domainId && !BelongsToDomain(GetField(list, "emailAddress"), domain)) continue;
            var current = await GetObjectAsync(cli, MailingListObject, GetField(list, "id"), env, cancellationToken) ?? throw MigrationMailError("lookup_unmeasured");
            resultLists.Add(new()
            {
                Address = GetField(current, "emailAddress"), Description = GetField(current, "description"), Recipients = ReadListRecipients(current),
                Aliases = ReadAliasAddresses(current, new Dictionary<string, string> { [domainId] = domain }), PostingPolicy = MailListPostingPolicy.Anyone
            });
        }
        var blockers = new List<string>();
        if ((await ReadDomainQuotasAsync(cancellationToken)).ContainsKey(domain)) blockers.Add("migration.mail_stale_domain_settings");
        if ((await ReadListPoliciesAsync(cancellationToken)).Keys.Any(address => BelongsToDomain(address, domain))) blockers.Add("migration.mail_stale_list_policy");
        if ((await ReadAutoRespondersAsync(cancellationToken)).Keys.Any(address => BelongsToDomain(address, domain))) blockers.Add("migration.mail_stale_autoresponder");
        var host = await ResolveClientHostNameAsync(cli, env, domain, cancellationToken);
        var listeners = await GetListenersAsync(cancellationToken);
        MigrationMailEndpoint? endpoint = null;
        if (listeners.Measured)
        {
            var ports = listeners.Listeners.Where(listener => listener.Protocol.Contains("imap", StringComparison.OrdinalIgnoreCase))
                .SelectMany(listener => IstemciyeUlasilabilirPortlar(listener.Binds)).ToHashSet();
            if (ports.Contains(993)) endpoint = new(host, 993, false);
            else if (ports.Contains(143)) endpoint = new(host, 143, true);
        }
        return new()
        {
            Ready = true, SchemaSupported = true, Version = version, DomainExists = domainId.Length > 0, DomainId = domainId,
            DomainActive = detail == null ? null : OkunanEtkinlik(detail.Value),
            ManualManagement = detail != null && new[] { "certificateManagement", "dkimManagement", "dnsManagement" }.All(field => detail.Value.TryGetProperty(field, out var management) && GetField(management, "@type") == "Manual"),
            Accounts = results, Aliases = aliases, Lists = resultLists, Endpoint = endpoint, Blockers = blockers
        };
    }
    public async Task<string> CreateDomainFreshAsync(string domain, CancellationToken cancellationToken)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);
        if (await FindIdAsync("domain", "name", domain, cancellationToken) != null) throw MigrationMailError("domain_exists");
        var manual = new Dictionary<string, object> { ["@type"] = "Manual" };
        await MigrationCreateAsync(cli, "create domain", JsonSerializer.Serialize(new
        { name = domain, isEnabled = true, certificateManagement = manual, dkimManagement = manual, dnsManagement = manual }), env, cancellationToken);
        return await FindIdAsync("domain", "name", domain, cancellationToken) ?? throw MigrationMailError("creation_unverified");
    }
    public async Task<string> CreateMailboxFreshAsync(string domainId, string address, string password, CancellationToken cancellationToken)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);
        if (await FindIdAsync("account", "emailAddress", address, cancellationToken) != null) throw MigrationMailError("account_exists");
        var credential = new Dictionary<string, object> { ["@type"] = "Password", ["secret"] = password };
        await MigrationCreateAsync(cli, "create Account/User", JsonSerializer.Serialize(new
        { name = address[..address.IndexOf('@')], domainId, credentials = new Dictionary<string, object> { ["1"] = credential } }), env, cancellationToken);
        return await FindIdAsync("account", "emailAddress", address, cancellationToken) ?? throw MigrationMailError("creation_unverified");
    }
    public async Task SetMailboxQuotaAsync(string accountId, string address, long quotaMb, CancellationToken cancellationToken)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var current = await GetObjectAsync(cli, "account", accountId, env, cancellationToken) ?? throw MigrationMailError("lookup_unmeasured");
        if (!GetField(current, "emailAddress").Equals(address, StringComparison.OrdinalIgnoreCase)) throw MigrationMailError("identity_changed");
        await MigrationCreateAsync(cli, "update account " + Arg(accountId), JsonSerializer.Serialize(new { quotas = new { maxDiskQuota = checked(quotaMb * 1024 * 1024) } }), env, cancellationToken);
    }
    public async Task SetMailboxActiveAsync(string accountId, string address, bool active, CancellationToken cancellationToken)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var current = await GetObjectAsync(cli, "account", accountId, env, cancellationToken) ?? throw MigrationMailError("lookup_unmeasured");
        if (!GetField(current, "emailAddress").Equals(address, StringComparison.OrdinalIgnoreCase)) throw MigrationMailError("identity_changed");
        var permissions = new Dictionary<string, object> { ["@type"] = "Merge", ["enabledPermissions"] = new Dictionary<string, bool>(),
            ["disabledPermissions"] = active ? new Dictionary<string, bool>() : new Dictionary<string, bool> { ["authenticate"] = true, ["emailReceive"] = true, ["emailSend"] = true } };
        await MigrationCreateAsync(cli, "update account " + Arg(accountId), JsonSerializer.Serialize(new { permissions }), env, cancellationToken);
    }
    public async Task SetDomainActiveAsync(string domainId, string domain, bool active, CancellationToken cancellationToken)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var current = await GetObjectAsync(cli, "domain", domainId, env, cancellationToken) ?? throw MigrationMailError("lookup_unmeasured");
        if (!GetField(current, "name").Equals(domain, StringComparison.OrdinalIgnoreCase)) throw MigrationMailError("identity_changed");
        await MigrationCreateAsync(cli, "update domain " + Arg(domainId), JsonSerializer.Serialize(new { isEnabled = active }), env, cancellationToken);
    }
    public async Task CreateAliasFreshAsync(string domainId, string accountId, string address, bool active, CancellationToken cancellationToken)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var current = await GetObjectAsync(cli, "account", accountId, env, cancellationToken) ?? throw MigrationMailError("lookup_unmeasured");
        if (GetField(current, "domainId") != domainId) throw MigrationMailError("identity_changed");
        var aliases = current.TryGetProperty("aliases", out var existing) && existing.ValueKind == JsonValueKind.Object
            ? existing.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal) : new Dictionary<string, JsonElement>();
        if (aliases.Values.Any(alias => GetField(alias, "name").Equals(address[..address.IndexOf('@')], StringComparison.OrdinalIgnoreCase) && GetField(alias, "domainId") == domainId)) throw MigrationMailError("alias_exists");
        using var entry = JsonDocument.Parse(JsonSerializer.Serialize(new { name = address[..address.IndexOf('@')], domainId, enabled = active }));
        aliases.Add(NextNumeric(aliases.Keys), entry.RootElement.Clone());
        await MigrationCreateAsync(cli, "update account " + Arg(accountId), JsonSerializer.Serialize(new { aliases }), env, cancellationToken);
    }
    public async Task<string> CreateListFreshAsync(string domainId, CreateMailListRequest request, IReadOnlyCollection<string> aliases, CancellationToken cancellationToken)
    {
        if (request.PostingPolicy != MailListPostingPolicy.Anyone || request.AllowedSenders.Count != 0) throw MigrationMailError("list_policy_unsupported");
        var (cli, env) = await RequireCliAsync(cancellationToken);
        if (await FindIdAsync(MailingListObject, "emailAddress", request.Address, cancellationToken) != null) throw MigrationMailError("list_exists");
        var aliasMap = aliases.Select((address, index) => (Key: (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Value: new { name = address[..address.IndexOf('@')], domainId, enabled = true }))
            .ToDictionary(item => item.Key, item => item.Value);
        await MigrationCreateAsync(cli, "create " + MailingListObject, JsonSerializer.Serialize(new
        { name = request.Address[..request.Address.IndexOf('@')], domainId, description = request.Description, aliases = aliasMap,
            recipients = request.Recipients.ToDictionary(address => address, _ => true, StringComparer.OrdinalIgnoreCase) }), env, cancellationToken);
        return await FindIdAsync(MailingListObject, "emailAddress", request.Address, cancellationToken) ?? throw MigrationMailError("creation_unverified");
    }
    private async Task<IReadOnlyList<JsonElement>> MigrationQueryAsync(string cli, string type, IReadOnlyDictionary<string, string> env, CancellationToken token)
    {
        var result = await QueryAsync(cli, type, env, token);
        if (!result.Measured) throw MigrationMailError("lookup_unmeasured");
        return result.Items.ToArray();
    }
    private async Task MigrationCreateAsync(string cli, string command, string json, IReadOnlyDictionary<string, string> env, CancellationToken token)
    {
        var result = await RunCliJsonAsync(cli, command, json, env, token);
        // Never propagate CLI output: a rejected credential payload may be echoed by a tool.
        if (!result.Success) throw MigrationMailError("provisioning_failed");
    }
    private static bool? MigrationAccountActive(JsonElement account)
    {
        if (!account.TryGetProperty("permissions", out var permissions)) return null;
        var kind = GetField(permissions, "@type");
        if (kind == "Inherit") return true;
        if (kind != "Merge" || !permissions.TryGetProperty("disabledPermissions", out var disabled) || disabled.ValueKind != JsonValueKind.Object) return null;
        var denied = new[] { "authenticate", "emailReceive", "emailSend" }.Count(key => disabled.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True);
        return denied == 3 ? false : denied == 0 ? true : null;
    }
    private static string NextNumeric(IEnumerable<string> keys) { var set = keys.ToHashSet(StringComparer.Ordinal); var index = 1; while (set.Contains(index.ToString())) index++; return index.ToString(); }
    private static AppException MigrationMailError(string code) => new("migration.mail_" + code, 409, 409);
}
