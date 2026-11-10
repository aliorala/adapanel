using System.Text.Json;
using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService
{
    private sealed class MigrationNativeZone
    {
        public bool Exists { get; set; }
        public string ZoneType { get; set; } = "";
        public string ZoneFile { get; set; } = "";
        public int UnsupportedRecords { get; set; }
        public MigrationDnsRecord[] Records { get; set; } = [];
    }
    private async Task<MigrationNativeZone> ReadMigrationNativeAsync(string zone, bool includeRecords, CancellationToken token)
    {
        var script = "$ErrorActionPreference='Stop'\n# ADAPANEL-MIGRATION-DNS-INVENTORY\n" + LiveRecordFunction + "\n" + MigrationTxtFunctions + "\n" +
            "$zoneName = '" + EscapePowerShellLiteral(zone) + "'\n" +
            "$zones = @(Get-DnsServerZone -ErrorAction Stop)\n$matches = @($zones | Where-Object { $_.ZoneName.TrimEnd('.') -ieq $zoneName })\n" +
            "if ($matches.Count -gt 1) { throw 'Ambiguous native zone.' }; $z = $matches | Select-Object -First 1\n" +
            "$records=@(); $unsupported=0\n" + (includeRecords ? """
            if ($z) {
              $raw = @(Get-DnsServerResourceRecord -ZoneName $zoneName -ErrorAction Stop)
              $unsupported = @($raw | Where-Object { [string]$_.RecordType -notin @('SOA','NS','A','AAAA','CNAME','MX','TXT','SRV','PTR','CAA','TLSA') -and -not ([string]$_.RecordType -eq 'Unknown' -and [int]$_.RecordData.Type -eq 257) }).Count
              $records = @(Get-AdaDnsRecords $zoneName | ForEach-Object { [pscustomobject]@{Type=$_.Type;Name=$_.Host;Value=$_.Value;Ttl=$_.Ttl;Priority=if($null -eq $_.Priority){0}else{$_.Priority}} })
            }
            """ : "") + "\n[pscustomobject]@{Exists=[bool]$z;ZoneType=if($z){[string]$z.ZoneType}else{''};ZoneFile=if($z){[string]$z.ZoneFile}else{''};UnsupportedRecords=$unsupported;Records=$records} | ConvertTo-Json -Depth 8 -Compress";
        var response = await (InventoryCommand ?? NativeCommand)(script, token);
        if (!response.Success) throw MigrationDnsError("native_inventory_unmeasured");
        try
        {
            using var document = JsonDocument.Parse(response.StandardOutput);
            foreach (var field in new[] { "Exists", "ZoneType", "ZoneFile", "UnsupportedRecords", "Records" })
                if (!document.RootElement.TryGetProperty(field, out _)) throw new JsonException();
            return JsonSerializer.Deserialize<MigrationNativeZone>(response.StandardOutput) ?? throw new JsonException();
        }
        catch (JsonException) { throw MigrationDnsError("native_inventory_unmeasured"); }
    }
    private static string MigrationPayloadPrelude(string file) => "$ErrorActionPreference='Stop'\n" +
        "$payload = Get-Content -LiteralPath '" + EscapePowerShellLiteral(file) + "' -Raw -Encoding UTF8 | ConvertFrom-Json\n" +
        "$zoneName = [string]$payload.ZoneName; $zoneFile=[string]$payload.ZoneFile\n";
    private const string MigrationOwnershipCheck = """
        function Assert-AdaMigrationZone {
          $z = Get-DnsServerZone -Name $zoneName -ErrorAction Stop
          if ([string]$z.ZoneType -ne 'Primary' -or $z.IsDsIntegrated -or [string]$z.ZoneFile -cne $zoneFile) { throw 'Native zone identity changed.' }
        }
        """;
    private static string MigrationCreateScript(string file) => MigrationPayloadPrelude(file) + "# ADAPANEL-MIGRATION-DNS-CREATE\n" + MigrationOwnershipCheck + "\n" + """
        $zones = @(Get-DnsServerZone -ErrorAction Stop)
        if (@($zones | Where-Object { $_.ZoneName.TrimEnd('.') -ieq $zoneName }).Count -ne 0) { throw 'Existing zone cannot be adopted.' }
        # Native CREATE is the final race boundary; no Force/LoadExisting/Convert/Set/Remove.
        $null = Add-DnsServerPrimaryZone -Name $zoneName -ZoneFile $zoneFile -DynamicUpdate None -PassThru -ErrorAction Stop
        Assert-AdaMigrationZone
        [pscustomobject]@{Created=$true;ZoneFile=$zoneFile} | ConvertTo-Json -Compress
        """;
    private static string MigrationAddScript(string file) => MigrationPayloadPrelude(file) + "# ADAPANEL-MIGRATION-DNS-ADD\n" + MigrationOwnershipCheck + "\n" + """
        $count=0
        foreach ($record in @($payload.Records)) {
          Assert-AdaMigrationZone
          # Official WMI method parses one complete RR. Payload is bound data, never executable.
          # https://learn.microsoft.com/windows/win32/dns/microsoftdns-resourcerecord-createinstancefromtextrepresentation
          $arguments=@{DnsServerName=$env:COMPUTERNAME;ContainerName=$zoneName;TextRepresentation=[string]$record.TextRepresentation}
          $result=Invoke-CimMethod -Namespace root/MicrosoftDNS -ClassName MicrosoftDNS_ResourceRecord -MethodName CreateInstanceFromTextRepresentation -Arguments $arguments -ErrorAction Stop
          if ($null -ne $result.ReturnValue -and [int]$result.ReturnValue -ne 0) { throw 'Native record creation failed.' }
          $count++
        }
        Assert-AdaMigrationZone
        [pscustomobject]@{Added=$count} | ConvertTo-Json -Compress
        """;
    // A TXT value is DNS octets, not a trimmed field or the first character-string.
    // Decode decimal escaped octets and UTF8 literals into one complete RR value.
    private const string MigrationTxtFunctions = """
        function Convert-AdaNativeTxt($r, $zone) {
          $owner=([string]$r.OwnerName).TrimEnd('.'); $suffix='.'+$zone.TrimEnd('.')
          if($owner -ieq $zone.TrimEnd('.')){$name='@'}
          elseif($owner.EndsWith($suffix,[StringComparison]::OrdinalIgnoreCase)){$name=$owner.Substring(0,$owner.Length-$suffix.Length)}
          else{throw 'TXT owner outside zone.'}
          $m=[regex]::Match([string]$r.TextRepresentation,'^\S+\s+(?:\d+\s+)?(?:IN\s+)?TXT\s+(?<data>.*)$','IgnoreCase,Singleline')
          if(-not $m.Success){throw 'Invalid TXT representation.'}
          $data=$m.Groups['data'].Value; $bytes=New-Object 'Collections.Generic.List[byte]'; $utf8=New-Object Text.UTF8Encoding($false,$true)
          $i=0; $parts=0
          while($i -lt $data.Length){
            while($i -lt $data.Length -and [char]::IsWhiteSpace($data[$i])){$i++}
            if($i -eq $data.Length){break}; if($data[$i] -ne '"'){throw 'TXT quote missing.'}
            $i++;$closed=$false;$literal=New-Object Text.StringBuilder
            while($i -lt $data.Length){
              $c=$data[$i];$i++
              if($c -eq '"'){$closed=$true;break}
              if($c -eq '\'){
                $bytes.AddRange([byte[]]$utf8.GetBytes($literal.ToString()));[void]$literal.Clear()
                if($i -eq $data.Length){throw 'TXT escape truncated.'}
                if($i+2 -lt $data.Length -and $data.Substring($i,3) -cmatch '^\d{3}$'){
                  $octet=[int]$data.Substring($i,3);if($octet -gt 255){throw 'TXT octet invalid.'};$bytes.Add([byte]$octet);$i+=3
                }else{[void]$literal.Append($data[$i]);$i++}
              }else{[void]$literal.Append($c)}
            }
            if(-not $closed){throw 'TXT string truncated.'}
            $bytes.AddRange([byte[]]$utf8.GetBytes($literal.ToString()));$parts++
            if($i -lt $data.Length -and -not [char]::IsWhiteSpace($data[$i])){throw 'TXT separator invalid.'}
          }
          if($parts -eq 0){throw 'TXT character-strings missing.'}
          [pscustomobject]@{Type='TXT';Host=$name;Value=$utf8.GetString($bytes.ToArray());Priority=$null;Ttl=[int]$r.TTL}
        }
        """;
}
