using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService
{
    public async Task<AdaPanel.Core.Dtos.Sites.SiteDnsZoneDto> EnsureMissingRecordsAsync(string siteName,
        IReadOnlyCollection<AdaPanel.Core.Dtos.Sites.UpsertDnsRecordRequest> requests, CancellationToken token = default)
    {
        var candidates = requests.Select(SnapshotRecordRequest).ToArray();
        using var zoneUse = await EnterZoneAsync(siteName, token);
        var state = await GetOrCreateStateAsync(zoneUse.Site!, token);
        EnsureZoneIsWritable(state);
        var before = state.Records.Count;
        foreach (var request in candidates)
        {
            var record = NormalizeRecord(request, state.ZoneName);
            record.IsManaged = true;
            // Automatic repair never adds another address/MX/DKIM policy beside a
            // user's existing RRset. TXT policies are separate from verification TXT.
            var prefix = ResolveSingletonTxtPrefix(record.Type, record.Value);
            if (state.Records.Any(old => old.Host == record.Host &&
                (old.Type == "CNAME" || record.Type == "CNAME" || old.Type == record.Type &&
                    (prefix is null || ResolveSingletonTxtPrefix(old.Type, old.Value) == prefix)))) continue;
            state.Records.Add(record);
        }
        if (state.Records.Count != before)
        {
            await UpsertStateAsync(state, token);
            await TrySyncWindowsDnsAsync(state, token);
        }
        return await ToDtoAsync(state, token);
    }

    internal Func<string, CancellationToken, Task<CommandResult>>? InventoryCommand { get; set; }
    // Used by both inventory and optimistic publication. Never interpolate RDATA into
    // executable expressions; user data is quoted with EscapePowerShellLiteral.
    private const string LiveRecordFunction = """
        function Convert-AdaDnsRecord($r) {
          $d = $r.RecordData
          $p = $null
          $t = [string]$r.RecordType
          if ($t -eq 'Unknown' -and [int]$d.Type -eq 257) {
            $raw = $d.Data
            if ($raw -is [Array]) { $bytes = [byte[]]$raw } else {
              $hex = ([string]$raw) -replace '\s',''
              if ($hex.Length % 2 -ne 0 -or $hex -notmatch '^[0-9a-fA-F]+$') { throw 'Invalid CAA RDATA.' }
              $bytes = [byte[]]@(for($i=0;$i -lt $hex.Length;$i+=2){[Convert]::ToByte($hex.Substring($i,2),16)})
            }
            if ($bytes.Length -lt 2 -or $bytes.Length -lt 2+[int]$bytes[1]) { throw 'Invalid CAA RDATA.' }
            $tag = [Text.Encoding]::ASCII.GetString($bytes,2,[int]$bytes[1])
            $caaValue = [Text.Encoding]::UTF8.GetString($bytes,2+[int]$bytes[1],$bytes.Length-2-[int]$bytes[1])
            return [pscustomobject]@{Type='CAA';Host=[string]$r.HostName;Value=('{0} {1} {2}' -f $bytes[0],$tag,$caaValue);Priority=$null;Ttl=[int]$r.TimeToLive.TotalSeconds}
          }
          $v = switch ($t) {
            'A' { [string]$d.IPv4Address }
            'AAAA' { [string]$d.IPv6Address }
            'CNAME' { ([string]$d.HostNameAlias).TrimEnd('.') }
            'NS' { ([string]$d.NameServer).TrimEnd('.') }
            'PTR' { ([string]$d.PtrDomainName).TrimEnd('.') }
            'MX' { $p = [int]$d.Preference; ([string]$d.MailExchange).TrimEnd('.') }
            'TXT' { $d.DescriptiveText -join '' }
            'SRV' { $p = [int]$d.Priority; '{0} {1} {2}' -f $d.Weight,$d.Port,([string]$d.DomainName).TrimEnd('.') }
            'TLSA' { $association = $d.CertificateAssociationData; if($association -is [byte[]]){$association=[BitConverter]::ToString($association).Replace('-','')}; '{0} {1} {2} {3}' -f $d.CertificateUsage,$d.Selector,$d.MatchingType,$association }
            default { return $null }
          }
          [pscustomobject]@{ Type=$t; Host=[string]$r.HostName; Value=[string]$v; Priority=$p; Ttl=[int]$r.TimeToLive.TotalSeconds }
        }
        """;

    private sealed class LiveZone
    {
        public bool Exists { get; set; }
        public string ZoneType { get; set; } = string.Empty;
        public List<DnsRecordState> Records { get; set; } = [];
    }

    private async Task RefreshLiveRecordsAsync(DnsZoneState state, bool persist, CancellationToken token,
        Func<CancellationToken, Task>? beforePublication = null)
    {
        if (!await IsWindowsDnsAvailableAsync(token)) return;
        var script = "$ErrorActionPreference='Stop'\n# ADAPANEL-LIVE-RECORDS\n" + LiveRecordFunction + "\n" +
            "$zones = @(Get-DnsServerZone -ErrorAction Stop)\n" +
            $"$z = $zones | Where-Object {{ $_.ZoneName.TrimEnd('.') -ieq '{EscapePowerShellLiteral(state.ZoneName)}' }}\n" +
            "$records = @(); if ($z) { $records = @(Get-DnsServerResourceRecord -ZoneName $z.ZoneName -ErrorAction Stop | ForEach-Object { Convert-AdaDnsRecord $_ } | Where-Object { $null -ne $_ }) }\n" +
            "[pscustomobject]@{ Exists=[bool]$z; ZoneType=if($z){[string]$z.ZoneType}else{''}; Records=$records } | ConvertTo-Json -Depth 6 -Compress";
        var response = await (InventoryCommand ?? NativeCommand)(script, token);
        if (!response.Success) throw new AppException("dns.sync_failed", 503, 503, BuildCommandDetails(response));
        LiveZone live;
        try
        {
            using var document = JsonDocument.Parse(response.StandardOutput);
            if (!document.RootElement.TryGetProperty("Exists", out _) && !document.RootElement.TryGetProperty("exists", out _))
                throw new JsonException("DNS inventory envelope missing.");
            live = JsonSerializer.Deserialize<LiveZone>(response.StandardOutput, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("DNS inventory missing.");
        }
        catch (JsonException exception) { throw new AppException("dns.sync_failed", 503, 503, exception.Message); }
        state.LiveZoneType = live.ZoneType;
        state.LiveInventoryMeasured = true;
        state.LiveRecords = live.Exists ? live.Records : null;
        if (!live.Exists) return; // Missing zone is distinct from a failed read.
        var old = state.Records.ToList();
        foreach (var record in live.Records)
        {
            record.Type = record.Type.ToUpperInvariant();
            record.Host = NormalizeHost(record.Host, state.ZoneName);
            var match = old.FirstOrDefault(item => NativeRecordKey(item) == NativeRecordKey(record));
            record.Id = match?.Id ?? Guid.NewGuid().ToString("N");
            record.IsManaged = match?.IsManaged ?? false;
        }
        state.Records = SortRecords(live.Records).ToList();
        state.LiveRecords = state.Records.Select(record => new DnsRecordState
        {
            Id = record.Id, Type = record.Type, Host = record.Host, Value = record.Value,
            Ttl = record.Ttl, Priority = record.Priority, IsManaged = record.IsManaged
        }).ToList();
        if (persist)
        {
            // Keep the pre-migration intent, including metadata, before replacing its cache.
            var backupPath = fileStore.ResolvePath("config/dns-zones.pre-native-authority.json");
            var originalPath = fileStore.ResolvePath(StatePath);
            if (File.Exists(originalPath) && !File.Exists(backupPath))
            {
                try { File.Copy(originalPath, backupPath, overwrite: false); }
                catch (IOException) when (File.Exists(backupPath)) { }
            }
            await UpsertStateAsync(state, token, beforePublication);
        }
    }

    private static string LivePredicate(DnsRecordState record)
    {
        var value = EscapePowerShellLiteral(record.Value);
        var host = EscapePowerShellLiteral(record.Host);
        var priority = record.Priority is null ? "$null -eq $a.Priority" : $"$a.Priority -eq {record.Priority}";
        return $"$a.Type -eq '{record.Type}' -and $a.Host -ieq '{host}' -and $a.Value -ceq '{value}' -and ({priority}) -and $a.Ttl -eq {record.Ttl}";
    }

    private static List<string> BuildLiveDeltaPrelude(DnsZoneState state, out List<DnsRecordState> additions)
    {
        var baseline = state.LiveRecords ?? [];
        additions = state.Records.Where(record => !baseline.Any(old => NativeRecordKey(old) == NativeRecordKey(record) && old.Ttl == record.Ttl)).ToList();
        var removals = baseline.Where(old => !state.Records.Any(record => NativeRecordKey(old) == NativeRecordKey(record) && old.Ttl == record.Ttl)).ToList();
        var lines = new List<string> { LiveRecordFunction };
        // Compare all records in each affected RRset BEFORE any removal/addition. A
        // concurrent native edit yields an explicit conflict rather than stale overwrite.
        foreach (var group in additions.Concat(removals).GroupBy(record => $"{record.Type}|{record.Host}"))
        {
            var sample = group.First();
            var expected = baseline.Where(record => record.Type == sample.Type && record.Host == sample.Host).ToArray();
            lines.Add($"$rr = @(Get-DnsServerResourceRecord -ZoneName $zoneName -ErrorAction Stop | Where-Object {{ $a = Convert-AdaDnsRecord $_; $null -ne $a -and $a.Type -eq '{sample.Type}' -and $a.Host -ieq '{EscapePowerShellLiteral(sample.Host)}' }})");
            lines.Add($"if ($rr.Count -ne {expected.Length}) {{ throw 'DNS changed concurrently; refresh and retry.' }}");
            foreach (var record in expected)
                lines.Add($"if (@($rr | Where-Object {{ $a = Convert-AdaDnsRecord $_; {LivePredicate(record)} }}).Count -ne 1) {{ throw 'DNS changed concurrently; refresh and retry.' }}");
        }
        foreach (var record in removals)
            lines.Add($"Get-DnsServerResourceRecord -ZoneName $zoneName -ErrorAction Stop | Where-Object {{ $a = Convert-AdaDnsRecord $_; $null -ne $a -and ({LivePredicate(record)}) }} | Remove-DnsServerResourceRecord -ZoneName $zoneName -Force -ErrorAction Stop");
        return lines;
    }
}
