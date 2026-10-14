using System.Net;
using System.Net.Sockets;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService
{
    private static List<DnsIpMappingDto> NormalizeIpMappings(IEnumerable<DnsIpMappingDto> mappings)
    {
        var result = new List<DnsIpMappingDto>();
        foreach (var mapping in mappings)
        {
            if (!IPAddress.TryParse(mapping.PrivateAddress?.Trim(), out var local) ||
                !IPAddress.TryParse(mapping.PublicAddress?.Trim(), out var published) ||
                local.AddressFamily != AddressFamily.InterNetwork || published.AddressFamily != AddressFamily.InterNetwork ||
                !IsPrivateIpv4(local) || IsPrivateIpv4(published) || IPAddress.IsLoopback(published) ||
                published.GetAddressBytes()[0] is 0 or >= 224 || published.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                throw new AppException("dns.invalid_ip_mapping", 400, 400);
            if (result.Any(item => item.PrivateAddress == local.ToString()) || result.Count >= 128)
                throw new AppException("dns.invalid_ip_mapping", 400, 400);
            result.Add(new DnsIpMappingDto { PrivateAddress = local.ToString(), PublicAddress = published.ToString() });
        }
        return result;
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private async Task<List<DnsIpMappingResultDto>> ApplyIpMappingsAsync(IReadOnlyList<DnsIpMappingDto> mappings,
        CancellationToken token, IReadOnlyList<DnsIpMappingDto>? previousMappings = null)
    {
        var results = new List<DnsIpMappingResultDto>();
        var replacements = mappings.ToDictionary(item => item.PrivateAddress, item => item.PublicAddress);
        var pending = await fileStore.ReadAsync("config/dns-ip-mapping-pending.json", new List<DnsIpMappingDto>(), token);
        foreach (var mapping in pending.Where(item => mappings.Any(current => current.PublicAddress == item.PublicAddress)))
            replacements.TryAdd(mapping.PrivateAddress, mapping.PublicAddress);
        foreach (var previous in previousMappings ?? [])
        {
            var current = mappings.FirstOrDefault(item => item.PrivateAddress == previous.PrivateAddress);
            if (current is not null && previous.PublicAddress != current.PublicAddress)
                replacements.TryAdd(previous.PublicAddress, current.PublicAddress);
        }
        var sites = await iisSiteService.GetSitesAsync(token);
        foreach (var site in sites)
        {
            var result = new DnsIpMappingResultDto { SiteName = site.Name };
            try
            {
                using var zoneUse = await EnterZoneAsync(site.Name, token);
                var state = await GetOrCreateStateAsync(zoneUse.Site!, token);
                var changes = state.Records.Where(record => record.Type == "A" && replacements.TryGetValue(record.Value, out var target) && record.Value != target).ToArray();
                if (changes.Length == 0) continue;
                if (state.IsSecondary || state.LiveZoneType == "Secondary")
                {
                    result.Error = "dns.zone_is_secondary";
                    results.Add(result);
                    continue;
                }
                await fileStore.WriteAsync($"config/dns-nat-backups/{state.ZoneName}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json", state, token);
                foreach (var record in changes) record.Value = replacements[record.Value];
                state.Records = state.Records.GroupBy(NativeRecordKey).Select(group => group.First()).ToList();
                var outcome = await TrySyncWindowsDnsAsync(state, token);
                result.ChangedRecords = changes.Length;
                result.Succeeded = outcome.Length == 0;
                result.Error = outcome;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.Error = exception is AppException app ? app.MessageKey + ": " + app.Details : "DNS mapping publication failed.";
            }
            results.Add(result);
        }
        return results;
    }
}
