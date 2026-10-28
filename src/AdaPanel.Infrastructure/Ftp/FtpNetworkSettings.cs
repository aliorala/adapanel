namespace AdaPanel.Infrastructure.Ftp;

public sealed partial class FtpService
{
    // Account provisioning and TLS policy changes reuse the same IIS site. Preserve the
    // administrator's NAT/range choices; initialize only IIS's unset network defaults.
    internal const string FtpNetworkSettingsScript = """
        $passiveFilter = 'system.ftpServer/firewallSupport'
        $passiveLow = (Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $passiveFilter -Name lowDataChannelPort -ErrorAction Stop).Value
        $passiveHigh = (Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $passiveFilter -Name highDataChannelPort -ErrorAction Stop).Value
        if ($null -eq $passiveLow -or $null -eq $passiveHigh) { throw 'FTP passive port range could not be measured.' }
        $passiveLow = [int]$passiveLow
        $passiveHigh = [int]$passiveHigh
        if ($passiveLow -eq 0 -and $passiveHigh -eq 0) {
          $passiveLow = $defaultPassiveLow
          $passiveHigh = $defaultPassiveHigh
          Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $passiveFilter -Name lowDataChannelPort -Value $passiveLow
          Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $passiveFilter -Name highDataChannelPort -Value $passiveHigh
        } elseif ($passiveLow -lt 1025 -or $passiveHigh -gt 65535 -or $passiveLow -gt $passiveHigh) {
          throw 'FTP passive port range is invalid; existing settings were preserved.'
        }
        $externalProperty = Get-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.firewallSupport.externalIp4Address -ErrorAction Stop
        if ($externalProperty -is [string]) { $existingExternalIp = $externalProperty }
        elseif ($null -ne $externalProperty -and $null -ne $externalProperty.PSObject.Properties['Value']) { $existingExternalIp = [string]$externalProperty.Value }
        else { throw 'FTP external IPv4 address could not be measured.' }
        if ([string]::IsNullOrWhiteSpace($existingExternalIp) -and -not [string]::IsNullOrWhiteSpace($defaultExternalIp)) {
          Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.firewallSupport.externalIp4Address -Value $defaultExternalIp
        }
        """;
}
