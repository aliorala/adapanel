namespace AdaPanel.Infrastructure.Ssl;

/// <summary>IIS binding snapshot guards. Does not inventory orphan HTTP.sys entries or native TLS policy.</summary>
internal static class SslBindingMutation
{
    internal sealed record ExpectedBinding(long SiteId, string PhysicalPath, string ApplicationPool,
        string Information, string Thumbprint, string StoreName, int Flags, string CandidateStore = "", bool MustBeAbsent = false);

    internal static string Script(string siteName, string hostName, int port, string thumbprint, ExpectedBinding? expected = null)
    {
        static string Literal(string value) => value.Replace("'", "''");
        var expectedGuard = expected is null ? "" : $$"""
            $approvedSite = @($script:sslMutationSites | Where-Object { $_.Name -ieq $site })
            if ($approvedSite.Count -ne 1 -or [long]$approvedSite[0].Id -ne {{expected.SiteId}} -or
                [Environment]::ExpandEnvironmentVariables([string]$approvedSite[0].physicalPath) -ine [Environment]::ExpandEnvironmentVariables('{{Literal(expected.PhysicalPath)}}') -or
                [string]$approvedSite[0].applicationPool -ine '{{Literal(expected.ApplicationPool)}}') { throw 'Approved site changed.' }
            if ({{(expected.MustBeAbsent ? "$true" : "$false")}}) {
              if (@($before | Where-Object { $_.Site -ieq $site -and $_.Port -eq $port -and $_.Host -ieq $dn }).Count -ne 0) { throw 'Approved absent binding changed.' }
            } else {
              $approvedBinding = @($before | Where-Object { $_.Site -ieq $site -and $_.Info -ieq '{{Literal(expected.Information)}}' })
              if ($approvedBinding.Count -ne 1 -or $approvedBinding[0].Hash -ine '{{Literal(expected.Thumbprint)}}' -or
                  $approvedBinding[0].Store -ine '{{Literal(expected.StoreName)}}' -or $approvedBinding[0].Flags -ne {{expected.Flags}}) { throw 'Approved binding changed.' }
            }
            $approvedStore = '{{Literal(expected.CandidateStore)}}'
            if ($approvedStore) {
              if ($null -eq (Stored-Certificate $thumb $approvedStore)) { throw 'Approved certificate store changed.' }
              $store = $approvedStore
            }
            """;
        return $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $ErrorActionPreference = 'Stop'
            $site = '{{Literal(siteName)}}'; $dn = '{{Literal(hostName)}}'; $port = {{port}}; $thumb = '{{Literal(thumbprint)}}'
            function Read-Hash($value) {
              if ($null -eq $value) { return '' }
              if ($value -is [byte[]]) { return [BitConverter]::ToString($value).Replace('-', '').ToLowerInvariant() }
              if ($value -isnot [string]) { throw 'Certificate hash is unreadable.' }
              $text = $value.Trim()
              if ($text -and $text -notmatch '^(?:[0-9a-fA-F]{2})+$') { throw 'Certificate hash is unreadable.' }
              return $text.ToLowerInvariant()
            }
            function Normalize-IP([string]$value) {
              if ($value -in @('', '*', '0.0.0.0')) { return '0.0.0.0' }
              $parsed = $null
              if (-not [Net.IPAddress]::TryParse($value.Trim('[', ']'), [ref]$parsed)) { throw 'Binding IP is unreadable.' }
              return $parsed.ToString()
            }
            function Snapshot($binding, [string]$owner) {
              $info = [string]$binding.bindingInformation
              if ($info -notmatch '^(.+):(\d+):([^:]*)$') { throw 'Binding identity is unreadable.' }
              $ip = $matches[1]; $number = [int]$matches[2]; $hostName = $matches[3]
              if ($number -lt 1 -or $number -gt 65535 -or $null -eq $binding.sslFlags) { throw 'Binding flags are unreadable.' }
              $flags = [int]$binding.sslFlags
              if ($flags -lt 0 -or (($flags -band 1) -ne 0 -and -not $hostName)) { throw 'Binding flags are inconsistent.' }
              $hash = Read-Hash $binding.certificateHash
              $rawStore = $binding.certificateStoreName
              if ($null -ne $rawStore -and $rawStore -isnot [string]) { throw 'Certificate store is unreadable.' }
              $storeName = [string]$rawStore
              [pscustomobject]@{ Site=$owner; Info=$info; IP=(Normalize-IP $ip); Port=$number; Host=$hostName; Flags=$flags; Hash=$hash; Store=$storeName }
            }
            function Read-Inventory {
              $script:sslMutationSites = @(Get-Website -ErrorAction Stop)
              foreach ($webSite in $script:sslMutationSites) {
                if (-not [string]$webSite.Name -or $null -eq $webSite.Bindings -or $null -eq $webSite.Bindings.Collection) { throw 'Site inventory is unreadable.' }
                foreach ($binding in @($webSite.Bindings.Collection)) {
                  if (-not [string]$binding.protocol) { throw 'Binding protocol is unreadable.' }
                  if ($binding.protocol -ieq 'https') { Snapshot $binding ([string]$webSite.Name) }
                }
              }
            }
            function Key($binding) {
              if (($binding.Flags -band 1) -ne 0) { return ('sni|' + $binding.Host.ToLowerInvariant() + '|' + $binding.Port) }
              return ('ip|' + $binding.IP + '|' + $binding.Port)
            }
            function Same-Identity($left, $right) { return ($left.Site -ieq $right.Site -and $left.Info -ieq $right.Info -and $left.Flags -eq $right.Flags) }
            function Same-Certificate($left, $right) { return ($left.Hash -ieq $right.Hash -and $left.Store -ieq $right.Store) }
            function Signature($bindings) {
              $semantic = @($bindings | ForEach-Object {
                [pscustomobject]@{ Site=$_.Site.ToLowerInvariant(); IP=$_.IP; Port=$_.Port; Host=$_.Host.ToLowerInvariant(); Flags=$_.Flags; Hash=$_.Hash.ToLowerInvariant(); Store=$_.Store.ToLowerInvariant() }
              } | Sort-Object Site,IP,Port,Host)
              return (ConvertTo-Json -InputObject $semantic -Depth 3 -Compress)
            }
            function Assert-Exclusive($inventory, $target) {
              $key = Key $target
              foreach ($item in $inventory) {
                if ($item.Site -ine $target.Site -and $item.Port -eq $target.Port -and $item.Host -ieq $target.Host) { throw 'Host and port are shared by another site.' }
                if ((Key $item) -eq $key -and -not (Same-Identity $item $target)) { throw 'TLS binding key is shared.' }
              }
            }
            function Assert-Inventory($expected, $actual) {
              if ((Signature $expected) -cne (Signature $actual)) { throw 'IIS binding snapshot changed.' }
            }
            function Other-Bindings($inventory, $target) { return @($inventory | Where-Object { -not ($_.Site -ieq $target.Site -and $_.Info -ieq $target.Info) }) }
            function Target-Bindings($inventory, $target) { return @($inventory | Where-Object { $_.Site -ieq $target.Site -and $_.Info -ieq $target.Info }) }
            function Stored-Certificate([string]$hash, [string]$storeName) {
              if ($storeName -notin @('My', 'WebHosting')) { throw 'Certificate store is unsupported.' }
              $path = 'Cert:\LocalMachine\' + $storeName + '\' + $hash
              if (-not (Test-Path -LiteralPath $path -ErrorAction Stop)) { return $null }
              $certificate = Get-Item -LiteralPath $path -ErrorAction Stop
              if ((Read-Hash $certificate.Thumbprint) -ine $hash -or $certificate.HasPrivateKey -isnot [bool] -or -not $certificate.HasPrivateKey) { return $null }
              return $certificate
            }
            function Raw-Target($target) {
              $items = @(Get-WebBinding -Name $site -Protocol https -ErrorAction Stop | Where-Object { $_.bindingInformation -ieq $target.Info })
              if ($items.Count -ne 1) { throw 'Exact binding could not be read.' }
              $snapshot = Snapshot $items[0] $site
              if (-not (Same-Identity $snapshot $target) -or -not (Same-Certificate $snapshot $target)) { throw 'Exact binding changed.' }
              return $items[0]
            }
            $thumb = Read-Hash $thumb
            if (-not $thumb -or $port -lt 1 -or $port -gt 65535) { throw 'Invalid certificate or port.' }
            $store = ''
            foreach ($candidate in @('My', 'WebHosting')) {
              if ($null -ne (Stored-Certificate $thumb $candidate)) { $store = $candidate; break }
            }
            if (-not $store) { throw 'LocalMachine certificate with a private key was not found.' }
            $before = @(Read-Inventory)
            {{expectedGuard}}
            if (@($script:sslMutationSites | Where-Object { $_.Name -ieq $site }).Count -ne 1) { throw 'Exact site was not found.' }
            $targets = @($before | Where-Object { $_.Site -ieq $site -and $_.Port -eq $port -and $_.Host -ieq $dn })
            if ($targets.Count -gt 1) { throw 'Binding IP is ambiguous.' }
            $existing = $targets.Count -eq 1
            if ($existing) {
              $old = $targets[0]
              if (($old.Flags -band 2) -ne 0) { throw 'Central certificate store bindings are unsupported.' }
              if (-not $old.Hash -or $null -eq (Stored-Certificate $old.Hash $old.Store)) { throw 'Previous certificate cannot be restored safely.' }
              if ($old.Hash -ieq $thumb) { $store = $old.Store }
            } else {
              $flags = if ($dn) { 1 } else { 0 }
              $old = [pscustomobject]@{ Site=$site; Info=('*:' + $port + ':' + $dn); IP='0.0.0.0'; Port=$port; Host=$dn; Flags=$flags; Hash=''; Store='' }
            }
            Assert-Exclusive $before $old
            if ($existing -and $old.Hash -ieq $thumb -and $old.Store -ieq $store) { 'OK'; return }
            Assert-Inventory $before @(Read-Inventory)
            $started = $false; $created = $false; $attachStarted = $false
            try {
              $expected = @($before)
              if (-not $existing) {
                $started = $true
                New-WebBinding -Name $site -Protocol https -IPAddress '*' -Port $port -HostHeader $dn -SslFlags $old.Flags -ErrorAction Stop | Out-Null
                $created = $true
                $createdInventory = @(Read-Inventory)
                Assert-Inventory $before @(Other-Bindings $createdInventory $old)
                $createdTargets = @(Target-Bindings $createdInventory $old)
                if ($createdTargets.Count -ne 1 -or -not (Same-Identity $createdTargets[0] $old) -or $createdTargets[0].Hash -or
                    $createdTargets[0].Store -notin @('', 'My', 'WebHosting')) { throw 'New binding is not an owned empty binding.' }
                $old = $createdTargets[0]
                $expected += $old
              }
              $fresh = @(Read-Inventory)
              Assert-Inventory $expected $fresh
              Assert-Exclusive $fresh $old
              $binding = Raw-Target $old
              $started = $true; $attachStarted = $true
              [void]$binding.AddSslCertificate($thumb, $store)
              $desired = [pscustomobject]@{ Site=$old.Site; Info=$old.Info; IP=$old.IP; Port=$old.Port; Host=$old.Host; Flags=$old.Flags; Hash=$thumb; Store=$store }
              Assert-Inventory (@(Other-Bindings $before $old) + @($desired)) @(Read-Inventory)
              'OK'
            } catch {
              if (-not $started) { throw }
              try {
                $current = @(Read-Inventory)
                Assert-Inventory @(Other-Bindings $before $old) @(Other-Bindings $current $old)
                Assert-Exclusive $current $old
                $owned = @(Target-Bindings $current $old)
                if ($existing) {
                  if ($owned.Count -ne 1 -or -not (Same-Identity $owned[0] $old)) { throw 'Rollback target changed.' }
                  $desiredCert = [pscustomobject]@{ Hash=$thumb; Store=$store }
                  if (-not (Same-Certificate $owned[0] $old) -and -not (Same-Certificate $owned[0] $desiredCert)) { throw 'Rollback certificate changed externally.' }
                  if ($null -eq (Stored-Certificate $old.Hash $old.Store)) { throw 'Rollback certificate is unavailable.' }
                  $binding = Raw-Target $owned[0]
                  [void]$binding.AddSslCertificate($old.Hash, $old.Store)
                } elseif ($owned.Count -gt 0) {
                  $desiredCert = [pscustomobject]@{ Hash=$thumb; Store=$store }
                  $emptyOwned = -not $owned[0].Hash -and $owned[0].Store -in @('', 'My', 'WebHosting')
                  $attachedOwned = $attachStarted -and (Same-Certificate $owned[0] $desiredCert)
                  if (-not $created -or $owned.Count -ne 1 -or -not (Same-Identity $owned[0] $old) -or
                      (-not $emptyOwned -and -not $attachedOwned)) { throw 'New binding changed externally.' }
                  $binding = Raw-Target $owned[0]
                  $binding | Remove-WebBinding -ErrorAction Stop
                }
                Assert-Inventory $before @(Read-Inventory)
              } catch { throw 'SSL_BINDING_ROLLBACK_UNVERIFIED' }
              throw 'SSL_BINDING_FAILED_ROLLED_BACK'
            }
            """;
    }
}
