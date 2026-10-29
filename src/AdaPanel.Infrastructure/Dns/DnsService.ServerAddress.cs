using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService
{
    private const string ServerAddressSyncPath = "config/server-address-dns-sync.json";
    private const string ServerAddressIntentPath = "config/server-address-dns-intent.json";
    private sealed class AddressIntent
    {
        public string ZoneName { get; set; } = string.Empty;
        public List<DnsRecordState> Records { get; set; } = [];
    }

    public Task<ServerAddressDnsSyncDto?> GetServerAddressDnsSyncAsync(CancellationToken cancellationToken = default)
        => fileStore.ReadAsync<ServerAddressDnsSyncDto?>(ServerAddressSyncPath, null, cancellationToken);

    public async Task PrepareServerAddressDnsSyncAsync(string oldAddress, string newAddress, CancellationToken cancellationToken = default)
    {
        if (!TryPublicIpv4(oldAddress, out var oldIp) || !TryPublicIpv4(newAddress, out var newIp) || oldIp == newIp)
            throw new AppException("dns.invalid_ip_mapping", 400, 400);
        using var syncUse = await DnsZoneLifecycle.EnterAsync(fileStore.ResolvePath(ServerAddressSyncPath), "migration", cancellationToken);
        var previous = await GetServerAddressDnsSyncAsync(cancellationToken);
        if (previous is { Pending: true })
        {
            if (previous.OldAddress != oldIp || previous.NewAddress != newIp)
                throw new AppException("dns.sync_failed", 409, 409, "Another server address migration is pending.");
            return;
        }
        await fileStore.WriteAsync(ServerAddressIntentPath, new List<AddressIntent>(), cancellationToken);
        await fileStore.WriteAsync(ServerAddressSyncPath, new ServerAddressDnsSyncDto { OldAddress = oldIp, NewAddress = newIp, Pending = true }, cancellationToken);
    }

    public async Task CancelPreparedServerAddressDnsSyncAsync(string oldAddress, string newAddress, CancellationToken cancellationToken = default)
    {
        using var syncUse = await DnsZoneLifecycle.EnterAsync(fileStore.ResolvePath(ServerAddressSyncPath), "migration", cancellationToken);
        var pending = await GetServerAddressDnsSyncAsync(cancellationToken);
        var intents = await fileStore.ReadAsync(ServerAddressIntentPath, new List<AddressIntent>(), cancellationToken);
        if (pending is { Pending: true, Zones.Count: 0 } && pending.OldAddress == oldAddress && pending.NewAddress == newAddress && intents.Count == 0)
            await fileStore.WriteAsync<ServerAddressDnsSyncDto?>(ServerAddressSyncPath, null, cancellationToken);
    }

    // The separate journal survives a settings save and failed/partial native publication.
    // A retry always reads native records again; cache values alone never prove success.
    public async Task<ServerAddressDnsSyncDto> SyncServerAddressAsync(string oldAddress, string newAddress,
        CancellationToken cancellationToken = default)
    {
        if (!TryPublicIpv4(oldAddress, out var oldIp) || !TryPublicIpv4(newAddress, out var newIp) || oldIp == newIp)
            throw new AppException("dns.invalid_ip_mapping", 400, 400);
        using var syncUse = await DnsZoneLifecycle.EnterAsync(fileStore.ResolvePath(ServerAddressSyncPath), "migration", cancellationToken);
        var previous = await GetServerAddressDnsSyncAsync(cancellationToken);
        if (previous is { Pending: true } && (previous.OldAddress != oldIp || previous.NewAddress != newIp))
            throw new AppException("dns.sync_failed", 409, 409, "Finish the pending server address migration before starting another.");
        var result = new ServerAddressDnsSyncDto { OldAddress = oldIp, NewAddress = newIp, Pending = true };
        var intents = previous is { Pending: true }
            ? await fileStore.ReadAsync(ServerAddressIntentPath, new List<AddressIntent>(), cancellationToken) : [];
        if (previous is not { Pending: true }) await fileStore.WriteAsync(ServerAddressIntentPath, intents, cancellationToken);
        await fileStore.WriteAsync(ServerAddressSyncPath, result, cancellationToken);
        try
        {
            // Keep future zone defaults consistent too. Do not run the broad NAT repair here.
            using (await DnsZoneLifecycle.EnterAsync(fileStore.ResolvePath(TemplatePath), "template", cancellationToken))
            {
                var template = await fileStore.ReadAsync(TemplatePath, new DnsTemplateDto(), cancellationToken);
                var templateChanges = template.Records.Select(record => (record, value: ReplaceServerAddress(record.Type, record.Value, oldIp, newIp)))
                    .Where(item => item.value != item.record.Value).ToArray();
                if (template.IpMappings.Any(mapping => mapping.PublicAddress == oldIp) || templateChanges.Length > 0)
                {
                    await fileStore.WriteAsync($"config/dns-address-backups/template-{Guid.NewGuid():N}.json", template, cancellationToken);
                    foreach (var mapping in template.IpMappings.Where(mapping => mapping.PublicAddress == oldIp)) mapping.PublicAddress = newIp;
                    foreach (var change in templateChanges) change.record.Value = change.value;
                    await fileStore.WriteAsync(TemplatePath, template, cancellationToken);
                }
            }
            var references = await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken);
            foreach (var reference in references.DistinctBy(state => NormalizeZoneName(state.ZoneName)))
            {
                var zoneResult = new ServerAddressDnsZoneResultDto { SiteName = reference.SiteName, ZoneName = reference.ZoneName };
                result.Zones.Add(zoneResult);
                var hasTargets = reference.Records.Any(record => ReplaceServerAddress(record.Type, record.Value, oldIp, newIp) != record.Value) ||
                    intents.Any(intent => SameNativeZone(intent.ZoneName, reference.ZoneName) && intent.Records.Count > 0);
                try
                {
                    // Existing references only: never seed or publish an unowned/missing zone.
                    using var zoneUse = await EnterZoneAsync(reference.SiteName, cancellationToken, readSite: false);
                    var currentReferences = await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken);
                    var state = currentReferences.FirstOrDefault(item => item.SiteName == reference.SiteName);
                    if (state is null) throw new InvalidOperationException("DNS zone reference was removed; retry with the current inventory.");
                    ApplySharedConfiguration(state, currentReferences);
                    EnsureSharedConfiguration(state);
                    if (state.IsSecondary) throw new InvalidOperationException("Secondary DNS zone must be updated on its primary server.");
                    await RefreshLiveRecordsAsync(state, persist: false, cancellationToken);
                    if (!state.LiveInventoryMeasured || state.LiveRecords is null || !string.Equals(state.LiveZoneType, "Primary", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A live primary DNS zone could not be verified; external/missing zones were not modified.");
                    var changes = state.Records.Select(record => (record, value: ReplaceServerAddress(record.Type, record.Value, oldIp, newIp)))
                        .Where(item => item.value != item.record.Value).ToArray();
                    zoneResult.ChangedRecords = changes.Length;
                    var intent = intents.FirstOrDefault(item => SameNativeZone(item.ZoneName, state.ZoneName));
                    if (changes.Length > 0 || intent is { Records.Count: > 0 })
                    {
                        await fileStore.WriteAsync($"config/dns-address-backups/{state.ZoneName}-{Guid.NewGuid():N}.json", state, cancellationToken);
                        foreach (var change in changes) change.record.Value = change.value;
                        // If the destination already exists, keep its metadata/TTL and remove
                        // the old duplicate. Two TTL expectations for one RDATA cannot publish.
                        state.Records = state.Records.GroupBy(NativeRecordKey).Select(group =>
                            group.FirstOrDefault(record => !changes.Any(change => ReferenceEquals(change.record, record))) ?? group.First()).ToList();
                        if (intent is null) { intent = new AddressIntent { ZoneName = state.ZoneName }; intents.Add(intent); }
                        foreach (var change in changes)
                            if (!intent.Records.Any(record => NativeRecordKey(record) == NativeRecordKey(change.record)))
                                intent.Records.Add(state.Records.First(record => NativeRecordKey(record) == NativeRecordKey(change.record)));
                        await fileStore.WriteAsync(ServerAddressIntentPath, intents, cancellationToken);
                        // A failed native replace can delete the old RR before adding the new one.
                        // Restore only our journaled targets, never broad defaults or cached RRsets.
                        foreach (var target in intent.Records)
                            if (!state.Records.Any(record => NativeRecordKey(record) == NativeRecordKey(target))) state.Records.Add(target);
                        var outcome = await TrySyncWindowsDnsAsync(state, cancellationToken);
                        if (outcome.Length > 0) throw new InvalidOperationException(outcome);
                        if (intent.Records.Any(target => !state.Records.Any(record => NativeRecordKey(record) == NativeRecordKey(target) && record.Ttl == target.Ttl)))
                            throw new InvalidOperationException("DNS migration targets were not confirmed by native readback.");
                    }
                    zoneResult.Succeeded = true;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    zoneResult.Error = BoundedError(exception);
                    // Unmeasured/unowned zones without any known migration target remain a
                    // warning, not an impossible-to-complete migration that blocks future saves.
                    zoneResult.Skipped = !hasTargets && zoneResult.ChangedRecords == 0;
                }
                await fileStore.WriteAsync(ServerAddressSyncPath, result, cancellationToken);
            }
            result.Pending = result.Zones.Any(zone => !zone.Succeeded && !zone.Skipped);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result.Error = BoundedError(exception);
        }
        try
        {
            result.Ftp = ftpService is null
                ? new AdaPanel.Core.Dtos.Ftp.FtpServerAddressSyncDto { Skipped = true, Error = "FTP service was not available for measurement." }
                : await ftpService.SyncServerAddressAsync(oldIp, newIp, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result.Ftp = new AdaPanel.Core.Dtos.Ftp.FtpServerAddressSyncDto { Error = BoundedError(exception) };
        }
        result.Pending = result.Error.Length > 0 || result.Zones.Any(zone => !zone.Succeeded && !zone.Skipped) ||
            result.Ftp is { Succeeded: false, Skipped: false };
        await fileStore.WriteAsync(ServerAddressSyncPath, result, cancellationToken);
        return result;
    }

    private static string BoundedError(Exception exception)
    {
        var message = SecretRedactor.Redact(exception is AppException app ? app.MessageKey + ": " + app.Details : exception.Message);
        return message.Length <= 1200 ? message : message[..1200];
    }

    internal static string ReplaceServerAddress(string type, string value, string oldAddress, string newAddress)
    {
        if (type == "A") return value == oldAddress ? newAddress : value;
        if (type != "TXT" || !Regex.IsMatch(value, @"^v=spf1(?:\s|$)", RegexOptions.IgnoreCase)) return value;
        // Exact mechanisms only. Preserve qualifiers, /32, whitespace and all other policies.
        return Regex.Replace(value, @"(?<=\s)([+?~-]?ip4:)" + Regex.Escape(oldAddress) + @"(/32)?(?=\s|$)",
            match => match.Groups[1].Value + newAddress + match.Groups[2].Value, RegexOptions.IgnoreCase);
    }

    internal static bool TryPublicIpv4(string value, out string normalized)
    {
        normalized = string.Empty;
        var text = value?.Trim() ?? string.Empty;
        // IPAddress also accepts legacy abbreviated/integer notation; settings migration must not.
        if (!Regex.IsMatch(text, @"^(?:\d{1,3}\.){3}\d{1,3}$") || !IPAddress.TryParse(text, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || IsPrivateIpv4(address) || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] is 0 or >= 224 || bytes[0] == 169 && bytes[1] == 254) return false;
        normalized = address.ToString();
        return normalized == text;
    }
}
