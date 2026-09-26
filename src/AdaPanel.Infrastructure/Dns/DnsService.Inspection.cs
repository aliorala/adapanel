using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService : IDnsInspectionService
{
    public async Task<IReadOnlyDictionary<string, DnsInspectionDto>> InspectZonesAsync(
        IReadOnlyCollection<SiteDto> sites, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, DnsInspectionDto>(StringComparer.OrdinalIgnoreCase);
        var prepared = new List<(SiteDto Site, DnsZoneState State, IReadOnlyList<string> Missing)>();
        foreach (var site in sites)
        {
            try
            {
                // A health read must not silently publish or upgrade DNS defaults.
                var state = await GetOrCreateStateAsync(site, cancellationToken, persist: false);
                IReadOnlyList<string> missing = [];
                if (!state.IsSecondary)
                {
                    var defaults = await BuildDefaultRecordsAsync(site, state.ZoneName,
                        await ResolvePreferredServerAddressAsync(cancellationToken), cancellationToken);
                    var before = state.Records.Select(RecordKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var working = state.Records.ToList();
                    MergeMissingDefaults(working, defaults);
                    missing = working.Where(record => !before.Contains(RecordKey(record)))
                        .Select(record => $"{record.Type} {record.Host}").ToArray();
                }
                prepared.Add((site, state, missing));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result[site.Name] = new DnsInspectionDto { Error = exception.Message };
            }
        }

        var probes = await DnsZoneInspectionProbe.ReadAsync(prepared.Select(item => item.State.ZoneName), cancellationToken, logger: logger);
        foreach (var (site, state, missing) in prepared)
        {
            var probe = probes[state.ZoneName];
            result[site.Name] = new DnsInspectionDto
            {
                Zone = new SiteDnsZoneDto
                {
                    SiteName = site.Name, ZoneName = state.ZoneName, IsSecondary = state.IsSecondary,
                    Records = SortRecords(state.Records).Select(ToDto).ToArray(),
                    IsWindowsDnsAvailable = probe.Available, IsWindowsZonePresent = probe.ZoneType.Length > 0,
                    WindowsZoneType = probe.ZoneType
                },
                MissingDefaults = missing,
                PublishedRecordKeys = probe.Keys?.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Error = probe.Error
            };
        }
        return result;
    }
}
