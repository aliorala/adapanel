using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService
{
    public async Task<ServerAddressDnsSyncDto?> ReconcileServerAddressAsync(string currentAddress,
        CancellationToken cancellationToken = default)
    {
        ServerAddressDnsSyncDto Skip(string warning = "") => new() { NewAddress = currentAddress, Skipped = true, Error = warning };
        if (!TryPublicIpv4(currentAddress, out var current)) return Skip();
        var previous = await GetServerAddressDnsSyncAsync(cancellationToken);
        if (previous is { Skipped: false } && previous.NewAddress == current &&
            TryPublicIpv4(previous.OldAddress, out var journalOld) && journalOld != current)
            return await SyncServerAddressAsync(journalOld, current, cancellationToken);
        if (previous is { Pending: true }) return previous; // Never replace another pending operation.

        var template = await fileStore.ReadAsync(TemplatePath, new DnsTemplateDto(), cancellationToken);
        string[] anchorHosts = ["@", "mail", "ns1", "ns2"];
        var anchors = template.Records.Where(record => record.Type.Equals("A", StringComparison.OrdinalIgnoreCase) &&
            anchorHosts.Contains(record.Host.Trim().ToLowerInvariant())).ToArray();
        var literalAddresses = anchors.Select(record => record.Value.Trim()).Where(value => TryPublicIpv4(value, out _))
            .Distinct(StringComparer.Ordinal).ToArray();
        // Placeholder defaults, an empty template, or an already-current template provide
        // no evidence of a previous public IP. This is a normal no-op, not a settings error.
        if (literalAddresses.Length == 0 || literalAddresses.All(address => address == current)) return Skip();
        if (literalAddresses.Length != 1 || anchors.Length != anchorHosts.Length ||
            anchorHosts.Any(host => anchors.Count(record => record.Host.Trim().Equals(host, StringComparison.OrdinalIgnoreCase)) != 1) ||
            anchors.Any(record => record.Value.Trim() != literalAddresses[0]))
            return Skip("Automatic DNS repair was skipped: default DNS template addresses are custom, incomplete or inconsistent; no previous server IP was inferred.");
        var candidate = literalAddresses[0];
        if (template.IpMappings.Select(mapping => mapping.PublicAddress).Distinct(StringComparer.Ordinal).Count() > 1 ||
            template.IpMappings.Any(mapping => mapping.PublicAddress != candidate))
            return Skip("Automatic DNS repair was skipped: NAT mappings disagree with the previous default server address.");

        var references = await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken);
        var verified = false;
        foreach (var reference in references.DistinctBy(state => NormalizeZoneName(state.ZoneName)))
        {
            try
            {
                using var zoneUse = await EnterZoneAsync(reference.SiteName, cancellationToken, readSite: false);
                var states = await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken);
                var state = states.FirstOrDefault(item => item.SiteName == reference.SiteName);
                if (state is null) continue;
                ApplySharedConfiguration(state, states);
                if (state.IsSecondary || HasConfigurationConflict(state)) continue;
                // Require saved panel ownership for two independent default anchors, including
                // the apex. Live-only/custom A values are never enough to infer an old IP.
                var owned = state.Records.Where(record => record.IsManaged && record.Type == "A" &&
                    record.Value == candidate && anchorHosts.Contains(record.Host)).ToArray();
                if (!owned.Any(record => record.Host == "@") || owned.Select(record => record.Host).Distinct().Count() < 2) continue;
                await RefreshLiveRecordsAsync(state, persist: false, cancellationToken);
                if (!state.LiveInventoryMeasured || state.LiveRecords is null ||
                    !state.LiveZoneType.Equals("Primary", StringComparison.OrdinalIgnoreCase)) continue;
                if (owned.All(expected => state.Records.Any(actual => NativeRecordKey(actual) == NativeRecordKey(expected))))
                { verified = true; break; }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Discovery is read-only and cannot turn a failed measurement into authority.
                logger.LogDebug(exception, "Previous DNS server address could not be verified for {Zone}", reference.ZoneName);
            }
        }
        return verified
            ? await SyncServerAddressAsync(candidate, current, cancellationToken)
            : Skip("Automatic DNS repair was skipped: the previous address could not be confirmed in panel-owned default records on a live primary DNS zone.");
    }
}
