namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService
{
    // DnsServerResourceRecordTxt.DescriptiveText is a scalar and Windows may expose
    // only its first character-string. WMI TextRepresentation describes the entire RR.
    // Keep each RR separate; concatenate only its own quoted character-strings.
    private const string NativeTxtFunctions = """
        function Convert-AdaTxtRdata([string]$text, [switch]$AsChunks) {
          $m = [regex]::Match($text, '^\S+\s+(?:\d+\s+)?(?:IN\s+)?TXT\s+(?<data>.*)$', 'IgnoreCase,Singleline')
          if (-not $m.Success) { throw 'Invalid native TXT representation.' }
          $data = $m.Groups['data'].Value
          $parts = New-Object 'Collections.Generic.List[string]'
          $i = 0
          while ($i -lt $data.Length) {
            while ($i -lt $data.Length -and [char]::IsWhiteSpace($data[$i])) { $i++ }
            if ($i -eq $data.Length) { break }
            if ($data[$i] -ne '"') { throw 'Invalid native TXT character-string.' }
            $i++; $closed = $false
            $part = New-Object Text.StringBuilder
            while ($i -lt $data.Length) {
              $c = $data[$i]; $i++
              if ($c -eq '"') { $closed = $true; break }
              if ($c -eq '\') {
                if ($i -eq $data.Length) { throw 'Invalid native TXT escape.' }
                if ($i+2 -lt $data.Length -and $data.Substring($i,3) -cmatch '^\d{3}$') {
                  $octet = [int]$data.Substring($i,3)
                  if ($octet -gt 255) { throw 'Invalid native TXT octet.' }
                  [void]$part.Append([char]$octet); $i += 3
                } else { [void]$part.Append($data[$i]); $i++ }
              } else { [void]$part.Append($c) }
            }
            if (-not $closed) { throw 'Unterminated native TXT character-string.' }
            if ($i -lt $data.Length -and -not [char]::IsWhiteSpace($data[$i])) { throw 'Invalid native TXT separator.' }
            $parts.Add($part.ToString())
          }
          if ($parts.Count -eq 0) { throw 'Native TXT character-strings missing.' }
          if ($AsChunks) { $parts.ToArray() } else { $parts -join '' }
        }
        function Get-AdaNativeTxt($zone) {
          # Filter zone membership in PowerShell: zone data never becomes WQL syntax.
          Get-CimInstance -Namespace root/MicrosoftDNS -ClassName MicrosoftDNS_TXTType -ErrorAction Stop |
            Where-Object { $_.ContainerName.TrimEnd('.') -ieq $zone.TrimEnd('.') }
        }
        function Convert-AdaNativeTxt($r, $zone) {
          $owner = ([string]$r.OwnerName).TrimEnd('.')
          $suffix = '.' + $zone.TrimEnd('.')
          if ($owner -ieq $zone.TrimEnd('.')) { $hostName = '@' }
          elseif ($owner.EndsWith($suffix,[StringComparison]::OrdinalIgnoreCase)) { $hostName = $owner.Substring(0,$owner.Length-$suffix.Length) }
          else { throw 'Native TXT owner outside zone.' }
          [pscustomobject]@{Type='TXT';Host=$hostName;Value=(Convert-AdaTxtRdata ([string]$r.TextRepresentation));Priority=$null;Ttl=[int]$r.TTL}
        }
        function Get-AdaDnsRecords($zone) {
          $native = @(Get-DnsServerResourceRecord -ZoneName $zone -ErrorAction Stop)
          $txt = @(Get-AdaNativeTxt $zone)
          if (@($native | Where-Object { [string]$_.RecordType -eq 'TXT' }).Count -ne $txt.Count) { throw 'Native TXT inventory is incomplete or changed concurrently.' }
          $native |
            Where-Object { [string]$_.RecordType -ne 'TXT' } |
            ForEach-Object { Convert-AdaDnsRecord $_ } | Where-Object { $null -ne $_ }
          $txt | ForEach-Object { Convert-AdaNativeTxt $_ $zone }
        }
        function Remove-AdaNativeTxtExact($zone, $name, $value, $ttl) {
          $matches = @(Get-AdaNativeTxt $zone | Where-Object {
            $a = Convert-AdaNativeTxt $_ $zone
            $a.Host -ieq $name -and $a.Value -ceq $value -and $a.Ttl -eq $ttl
          })
          if ($matches.Count -ne 1) { throw ('TXT deletion requires exactly one complete match; found {0}.' -f $matches.Count) }
          # Preserve native character-string boundaries. CimInstance identity/RecordData
          # is not used to identify the record passed to the DNS deletion operation.
          [string[]]$data = @(Convert-AdaTxtRdata ([string]$matches[0].TextRepresentation) -AsChunks)
          if ($null -eq $data -or $data.Count -lt 1) { throw 'TXT deletion requires explicit character-strings.' }
          Remove-DnsServerResourceRecord -ZoneName $zone -RRType TXT -Name $name -RecordData $data -Force -ErrorAction Stop
        }
        """;
}
