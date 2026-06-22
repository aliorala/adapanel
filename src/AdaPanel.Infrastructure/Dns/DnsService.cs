using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService(
    IIisSiteService iisSiteService,
    IPanelPreferencesService panelPreferencesService,
    JsonFileStore fileStore) : IDnsService
{
    private const string StatePath = "config/dns-zones.json";
    private const int CurrentDefaultsVersion = 1;
    private static readonly string[] SupportedTypes = ["A", "AAAA", "CNAME", "MX", "TXT", "NS"];

    public async Task<SiteDnsZoneDto> GetZoneAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task EnsureZonePublishedAsync(string siteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            var state = await GetOrCreateStateAsync(site, cancellationToken);
            await TrySyncWindowsDnsAsync(state, cancellationToken);
        }
        catch
        {
            // Best-effort: Windows DNS yoksa/yazilamazsa site olusturmayi bozmamak icin sessiz gec.
        }
    }

    public async Task<SiteDnsZoneDto> InstallWindowsDnsAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);

        if (!await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            // Install-WindowsFeature yalnız Windows Server'da vardir (istemci OS'ta yok).
            var install = await ProcessRunner.RunPowerShellAsync(
                "if (-not (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue)) { exit 21 }; " +
                "Install-WindowsFeature -Name DNS -IncludeManagementTools | Out-Null",
                cancellationToken);

            if (install.ExitCode == 21)
            {
                throw new AppException(
                    "dns.windows_install_unsupported",
                    400,
                    400,
                    "Install-WindowsFeature is not available. The Windows DNS Server role requires Windows Server.");
            }

            if (!install.Success)
            {
                throw new AppException("dns.windows_install_failed", 500, 500, BuildCommandDetails(install));
            }
        }

        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> UpdateZoneAsync(string siteName, UpdateDnsZoneRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        state.ZoneName = NormalizeZoneName(string.IsNullOrWhiteSpace(request.ZoneName) ? state.ZoneName : request.ZoneName);
        state.NameServers = NormalizeNameServers(request.NameServers, state.ZoneName);
        state.Records.RemoveAll(record => record.IsManaged);
        state.Records.AddRange(CreateDefaultRecords(site, state.ZoneName, serverAddress));
        state.DefaultsVersion = CurrentDefaultsVersion;
        state.Records = SortRecords(state.Records).ToList();

        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> ResetDefaultsAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        state.Records.RemoveAll(record => record.IsManaged);
        state.Records.AddRange(CreateDefaultRecords(site, state.ZoneName, serverAddress));
        state.DefaultsVersion = CurrentDefaultsVersion;
        state.Records = SortRecords(state.Records).ToList();

        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        var record = NormalizeRecord(request, state.ZoneName);
        record.IsManaged = false;

        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            var index = state.Records.FindIndex(item => string.Equals(item.Id, request.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new AppException("dns.record_not_found", 404, 404);
            }

            record.Id = state.Records[index].Id;
            state.Records[index] = record;
        }
        else
        {
            var duplicate = state.Records.FirstOrDefault(item =>
                string.Equals(item.Type, record.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Host, record.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Value, record.Value, StringComparison.OrdinalIgnoreCase));

            if (duplicate is not null)
            {
                record.Id = duplicate.Id;
                state.Records[state.Records.IndexOf(duplicate)] = record;
            }
            else
            {
                state.Records.Add(record);
            }
        }

        state.Records = SortRecords(state.Records).ToList();
        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> DeleteRecordAsync(string siteName, string recordId, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        var removed = state.Records.RemoveAll(item => string.Equals(item.Id, recordId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            throw new AppException("dns.record_not_found", 404, 404);
        }

        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task DeleteZoneAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken)).ToList();
        var zone = states.FirstOrDefault(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
        {
            return;
        }

        states.RemoveAll(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));
        await fileStore.WriteAsync(StatePath, states.OrderBy(item => item.SiteName, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);

        // Windows DNS'te bölge varsa best-effort kaldır (yoksa sessiz geç).
        if (!string.IsNullOrWhiteSpace(zone.ZoneName) && await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            var script =
                "$ErrorActionPreference = 'SilentlyContinue'\n" +
                $"$zoneName = '{EscapePowerShellLiteral(zone.ZoneName)}'\n" +
                "if (Get-DnsServerZone -Name $zoneName -ErrorAction SilentlyContinue) { Remove-DnsServerZone -Name $zoneName -Force -ErrorAction SilentlyContinue }";
            try
            {
                await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            }
            catch
            {
                // best-effort: panel DNS kaydı zaten kaldırıldı.
            }
        }
    }

    private async Task<DnsZoneState> GetOrCreateStateAsync(SiteDto site, CancellationToken cancellationToken)
    {
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken);
        var state = states.FirstOrDefault(item => string.Equals(item.SiteName, site.Name, StringComparison.OrdinalIgnoreCase));
        if (state is not null)
        {
            state.ZoneName = NormalizeZoneName(state.ZoneName);
            state.NameServers = NormalizeNameServers(state.NameServers, state.ZoneName);
            state.Records = SortRecords(state.Records.Select(record => NormalizeRecord(record, state.ZoneName))).ToList();
            if (state.DefaultsVersion < CurrentDefaultsVersion)
            {
                MergeMissingDefaults(state.Records, CreateDefaultRecords(site, state.ZoneName, serverAddress));
                state.DefaultsVersion = CurrentDefaultsVersion;
                state.Records = SortRecords(state.Records).ToList();
                await UpsertStateAsync(state, cancellationToken);
                await TrySyncWindowsDnsAsync(state, cancellationToken);
            }

            return state;
        }

        var zoneName = ResolveDefaultZoneName(site);
        state = new DnsZoneState
        {
            SiteName = site.Name,
            ZoneName = zoneName,
            NameServers = NormalizeNameServers([], zoneName),
            Records = CreateDefaultRecords(site, zoneName, serverAddress).ToList(),
            DefaultsVersion = CurrentDefaultsVersion
        };
        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return state;
    }

    private async Task UpsertStateAsync(DnsZoneState state, CancellationToken cancellationToken)
    {
        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken)).ToList();
        states.RemoveAll(item => string.Equals(item.SiteName, state.SiteName, StringComparison.OrdinalIgnoreCase));
        states.Add(state);
        await fileStore.WriteAsync(StatePath, states.OrderBy(item => item.SiteName, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
    }

    private async Task<SiteDnsZoneDto> ToDtoAsync(DnsZoneState state, CancellationToken cancellationToken)
    {
        var isDnsAvailable = await IsWindowsDnsAvailableAsync(cancellationToken);
        var isZonePresent = isDnsAvailable && await IsWindowsZonePresentAsync(state.ZoneName, cancellationToken);
        return new SiteDnsZoneDto
        {
            SiteName = state.SiteName,
            ZoneName = state.ZoneName,
            NameServers = state.NameServers,
            Records = SortRecords(state.Records).Select(ToDto).ToArray(),
            IsWindowsDnsAvailable = isDnsAvailable,
            IsWindowsZonePresent = isZonePresent,
            Source = isDnsAvailable ? "windows-dns" : "panel-managed",
            StatusMessage = isDnsAvailable
                ? isZonePresent ? "dns.windows_zone_present" : "dns.windows_zone_missing"
                : "dns.windows_dns_unavailable"
        };
    }

    private static SiteDnsRecordDto ToDto(DnsRecordState state)
    {
        return new SiteDnsRecordDto
        {
            Id = state.Id,
            Type = state.Type,
            Host = state.Host,
            Value = state.Value,
            Ttl = state.Ttl,
            Priority = state.Priority,
            IsManaged = state.IsManaged
        };
    }

    private static IEnumerable<DnsRecordState> CreateDefaultRecords(SiteDto site, string zoneName, string preferredServerAddress)
    {
        var serverIp = ResolveDefaultIpv4Address(site, preferredServerAddress);
        yield return CreateManagedRecord("NS", "@", $"ns1.{zoneName}");
        yield return CreateManagedRecord("NS", "@", $"ns2.{zoneName}");
        yield return CreateManagedRecord("CNAME", "www", zoneName);
        yield return CreateManagedRecord("CNAME", "ftp", zoneName);

        if (!string.IsNullOrWhiteSpace(serverIp))
        {
            yield return CreateManagedRecord("A", "@", serverIp);
            yield return CreateManagedRecord("A", "ns1", serverIp);
            yield return CreateManagedRecord("A", "ns2", serverIp);
        }
    }

    private static DnsRecordState CreateManagedRecord(string type, string host, string value)
    {
        return new DnsRecordState
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            Host = host,
            Value = value,
            Ttl = 3600,
            IsManaged = true
        };
    }

    private static void MergeMissingDefaults(List<DnsRecordState> records, IEnumerable<DnsRecordState> defaults)
    {
        foreach (var defaultRecord in defaults)
        {
            var exists = records.Any(record =>
                string.Equals(record.Type, defaultRecord.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.Host, defaultRecord.Host, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                records.Add(defaultRecord);
            }
        }
    }

    private static string ResolveDefaultIpv4Address(SiteDto site, string preferredServerAddress)
    {
        var explicitIp = site.Bindings
            .Select(binding => binding.IpAddress)
            .FirstOrDefault(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value != "*" &&
                IPAddress.TryParse(value, out var address) &&
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address));

        if (!string.IsNullOrWhiteSpace(explicitIp))
        {
            return explicitIp;
        }

        if (IPAddress.TryParse(preferredServerAddress, out var preferredAddress) &&
            preferredAddress.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(preferredAddress))
        {
            return preferredAddress.ToString();
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(address => address.ToString())
            .FirstOrDefault() ?? string.Empty;
    }

    private async Task<string> ResolvePreferredServerAddressAsync(CancellationToken cancellationToken)
    {
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        return preferences.ServerAddress;
    }

    private async Task TrySyncWindowsDnsAsync(DnsZoneState state, CancellationToken cancellationToken)
    {
        if (!await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            return;
        }

        var script = BuildSyncScript(state);
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("dns.sync_failed", 500, 500, BuildCommandDetails(result));
        }
    }

    private static string BuildSyncScript(DnsZoneState state)
    {
        var groups = state.Records
            .GroupBy(record => $"{record.Type}|{record.Host}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Type = group.First().Type.ToUpperInvariant(), Host = group.First().Host })
            .ToArray();

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"$zoneName = '{EscapePowerShellLiteral(state.ZoneName)}'",
            "if (-not (Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue)) { exit 21 }",
            "if (-not (Get-DnsServerZone -Name $zoneName -ErrorAction SilentlyContinue)) {",
            "  Add-DnsServerPrimaryZone -Name $zoneName -ZoneFile \"$zoneName.dns\" | Out-Null",
            "}"
        };

        foreach (var group in groups)
        {
            lines.Add($"Get-DnsServerResourceRecord -ZoneName $zoneName -Name '{EscapePowerShellLiteral(group.Host)}' -RRType '{group.Type}' -ErrorAction SilentlyContinue | Remove-DnsServerResourceRecord -ZoneName $zoneName -Force -ErrorAction SilentlyContinue");
        }

        foreach (var record in SortRecords(state.Records))
        {
            var host = EscapePowerShellLiteral(record.Host);
            var value = EscapePowerShellLiteral(record.Value);
            var ttl = Math.Clamp(record.Ttl, 60, 86400);
            lines.Add(record.Type.ToUpperInvariant() switch
            {
                "A" => $"Add-DnsServerResourceRecordA -ZoneName $zoneName -Name '{host}' -IPv4Address '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "AAAA" => $"Add-DnsServerResourceRecordAAAA -ZoneName $zoneName -Name '{host}' -IPv6Address '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "CNAME" => $"Add-DnsServerResourceRecordCName -ZoneName $zoneName -Name '{host}' -HostNameAlias '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "MX" => $"Add-DnsServerResourceRecordMX -ZoneName $zoneName -Name '{host}' -MailExchange '{value}' -Preference {record.Priority ?? 10} -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "TXT" => $"Add-DnsServerResourceRecord -ZoneName $zoneName -Txt -Name '{host}' -DescriptiveText '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "NS" => $"Add-DnsServerResourceRecord -ZoneName $zoneName -NS -Name '{host}' -NameServer '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                _ => string.Empty
            });
        }

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static DnsRecordState NormalizeRecord(UpsertDnsRecordRequest request, string zoneName)
    {
        return NormalizeRecord(new DnsRecordState
        {
            Id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id.Trim(),
            Type = request.Type,
            Host = request.Host,
            Value = request.Value,
            Ttl = request.Ttl,
            Priority = request.Priority
        }, zoneName);
    }

    private static DnsRecordState NormalizeRecord(DnsRecordState record, string zoneName)
    {
        var type = record.Type.Trim().ToUpperInvariant();
        if (!SupportedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("dns.invalid_record_type", 400, 400);
        }

        var host = NormalizeHost(record.Host, zoneName);
        var value = record.Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppException("dns.invalid_record_value", 400, 400);
        }

        ValidateRecordValue(type, value, record.Priority);
        return new DnsRecordState
        {
            Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id,
            Type = type,
            Host = host,
            Value = NormalizeRecordValue(type, value),
            Ttl = Math.Clamp(record.Ttl <= 0 ? 3600 : record.Ttl, 60, 86400),
            Priority = type == "MX" ? Math.Clamp(record.Priority ?? 10, 0, 65535) : null,
            IsManaged = record.IsManaged
        };
    }

    private static void ValidateRecordValue(string type, string value, int? priority)
    {
        switch (type)
        {
            case "A" when !IsAddressFamily(value, System.Net.Sockets.AddressFamily.InterNetwork):
                throw new AppException("dns.invalid_record_value", 400, 400);
            case "AAAA" when !IsAddressFamily(value, System.Net.Sockets.AddressFamily.InterNetworkV6):
                throw new AppException("dns.invalid_record_value", 400, 400);
            case "CNAME":
            case "NS":
                _ = NormalizeDomainValue(value);
                break;
            case "MX":
                _ = NormalizeDomainValue(value);
                if (priority is < 0 or > 65535)
                {
                    throw new AppException("dns.invalid_priority", 400, 400);
                }
                break;
            case "TXT" when value.Length > 512:
                throw new AppException("dns.invalid_record_value", 400, 400);
        }
    }

    private static bool IsAddressFamily(string value, System.Net.Sockets.AddressFamily family)
    {
        return IPAddress.TryParse(value, out var address) && address.AddressFamily == family;
    }

    private static string NormalizeRecordValue(string type, string value)
    {
        return type is "CNAME" or "MX" or "NS"
            ? NormalizeDomainValue(value)
            : value.Trim();
    }

    private static string ResolveDefaultZoneName(SiteDto site)
    {
        var hostName = site.Bindings
            .Select(binding => binding.HostName)
            .FirstOrDefault(host => !string.IsNullOrWhiteSpace(host) && host.Contains('.'));

        if (!string.IsNullOrWhiteSpace(hostName))
        {
            var normalizedHost = NormalizeDomainValue(hostName);
            return normalizedHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? normalizedHost[4..]
                : normalizedHost;
        }

        return NormalizeZoneName(site.Name.Contains('.') ? site.Name : $"{site.Name}.local");
    }

    private static string NormalizeZoneName(string value)
    {
        var normalized = NormalizeDomainValue(value);
        if (!normalized.Contains('.'))
        {
            throw new AppException("dns.invalid_zone", 400, 400);
        }

        return normalized;
    }

    private static string NormalizeDomainValue(string value)
    {
        var normalized = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (!DomainPattern().IsMatch(normalized))
        {
            throw new AppException("dns.invalid_zone", 400, 400);
        }

        return normalized;
    }

    private static string NormalizeHost(string value, string zoneName)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "@" : value.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.Equals(normalized, zoneName, StringComparison.OrdinalIgnoreCase))
        {
            return "@";
        }

        if (normalized.EndsWith($".{zoneName}", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^(zoneName.Length + 1)];
        }

        if (normalized != "@" && !HostPattern().IsMatch(normalized))
        {
            throw new AppException("dns.invalid_host", 400, 400);
        }

        return normalized;
    }

    private static string[] NormalizeNameServers(string[] nameServers, string zoneName)
    {
        var normalized = nameServers
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Select(NormalizeDomainValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length > 0
            ? normalized
            : [$"ns1.{zoneName}", $"ns2.{zoneName}"];
    }

    private static IEnumerable<DnsRecordState> SortRecords(IEnumerable<DnsRecordState> records)
    {
        return records
            .OrderBy(record => Array.IndexOf(SupportedTypes, record.Type.ToUpperInvariant()))
            .ThenBy(record => record.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsWindowsDnsAvailableAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            "if (Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue) { 'true' } else { 'false' }",
            cancellationToken);

        return result.Success && result.StandardOutput.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsWindowsZonePresentAsync(string zoneName, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            $"if (Get-DnsServerZone -Name '{EscapePowerShellLiteral(zoneName)}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}",
            cancellationToken);

        return result.Success && result.StandardOutput.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''");
    }

    private static string BuildCommandDetails(CommandResult result)
    {
        var details = $"""
            ExitCode={result.ExitCode}
            StandardOutput:
            {result.StandardOutput}
            StandardError:
            {result.StandardError}
            """;

        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private sealed class DnsZoneState
    {
        public string SiteName { get; set; } = string.Empty;

        public string ZoneName { get; set; } = string.Empty;

        public string[] NameServers { get; set; } = [];

        public List<DnsRecordState> Records { get; set; } = [];

        public int DefaultsVersion { get; set; }
    }

    private sealed class DnsRecordState
    {
        public string Id { get; set; } = string.Empty;

        public string Type { get; set; } = "A";

        public string Host { get; set; } = "@";

        public string Value { get; set; } = string.Empty;

        public int Ttl { get; set; } = 3600;

        public int? Priority { get; set; }

        public bool IsManaged { get; set; }
    }

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.Compiled)]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"^(?:@|\*|[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*)$", RegexOptions.Compiled)]
    private static partial Regex HostPattern();
}
