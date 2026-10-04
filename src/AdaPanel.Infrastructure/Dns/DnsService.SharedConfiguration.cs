using System.Net;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService
{
    private const string SharedConfigurationConflictKey = "dns.shared_configuration_conflict";

    // Settings belong to the native zone. Records and their ownership stay on each site.
    // Legacy disagreement is not resolved by choosing whichever site happened to be read first.
    private static void ApplySharedConfiguration(DnsZoneState state, IEnumerable<DnsZoneState> states, bool newReference = false)
    {
        var peers = states.Where(item => SameNativeZone(item.ZoneName, state.ZoneName) &&
            !string.Equals(item.SiteName, state.SiteName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var members = newReference && peers.Length > 0 ? peers : peers.Append(state).ToArray();
        state.IsSharedZone = peers.Length > 0;
        state.ModeConfigurationConflict = members.Select(item => item.IsSecondary).Distinct().Count() > 1;
        var mode = members[0].IsSecondary;
        // Saved masters are inactive in primary mode and need not agree until explicitly selected.
        if (mode && !state.ModeConfigurationConflict)
            state.ModeConfigurationConflict = members.Select(item => IpSetKey(item.MasterServers)).Distinct(StringComparer.Ordinal).Count() > 1;
        state.TransferConfigurationConflict = members.Select(item => IpSetKey(item.ZoneTransferIps)).Distinct(StringComparer.Ordinal).Count() > 1;
        if (!state.ModeConfigurationConflict)
        {
            state.IsSecondary = mode;
            if (mode) state.MasterServers = CanonicalIps(members[0].MasterServers).ToList();
        }
        if (!state.TransferConfigurationConflict)
            state.ZoneTransferIps = CanonicalIps(members[0].ZoneTransferIps).ToList();
    }

    private static string[] CanonicalIps(IEnumerable<string>? addresses) => (addresses ?? [])
        .Select(value => IPAddress.Parse(value.Trim()).ToString()).Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal).ToArray();

    private static string IpSetKey(IEnumerable<string>? addresses) => string.Join('\n', CanonicalIps(addresses));
    private static bool HasConfigurationConflict(DnsZoneState state)
        => state.ModeConfigurationConflict || state.TransferConfigurationConflict;

    private static void EnsureSharedConfiguration(DnsZoneState state)
    {
        if (HasConfigurationConflict(state)) throw new AppException(SharedConfigurationConflictKey, 409, 409);
    }

    private async Task ApproveConfigurationAsync(string siteName, IReadOnlyCollection<string> zoneNames,
        Func<IReadOnlyList<string>, CancellationToken, Task>? approval, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (approval is null) return;
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken);
        var names = states.Where(item => zoneNames.Any(zone => SameNativeZone(item.ZoneName, zone)))
            .Select(item => item.SiteName).Append(siteName).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        await approval(Array.AsReadOnly(names), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task ApproveNativeConfigurationAsync(Func<CancellationToken, Task>? approval,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (approval is not null) await approval(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task SaveSharedConfigurationAsync(DnsZoneState state, bool updateMode, bool updateTransfer,
        Func<CancellationToken, Task>? approval, CancellationToken cancellationToken)
    {
        var updated = await fileStore.UpdateAsync(StatePath, Array.Empty<DnsZoneState>(), current =>
        {
            var result = current.Where(item => !string.Equals(item.SiteName, state.SiteName, StringComparison.OrdinalIgnoreCase))
                .Append(state).OrderBy(item => item.SiteName, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var member in result.Where(item => SameNativeZone(item.ZoneName, state.ZoneName)))
            {
                if (updateMode)
                {
                    member.IsSecondary = state.IsSecondary;
                    if (state.IsSecondary) member.MasterServers = [.. state.MasterServers];
                }
                if (updateTransfer) member.ZoneTransferIps = [.. state.ZoneTransferIps];
            }
            return result;
        }, approval, cancellationToken);
        ApplySharedConfiguration(state, updated);
    }
}
