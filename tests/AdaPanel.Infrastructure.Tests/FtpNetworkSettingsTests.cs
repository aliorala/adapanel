using System.Text.Json;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FtpNetworkSettingsTests
{
    [Theory]
    [InlineData(52000, 52500, "198.51.100.45", false)]
    [InlineData(50000, 51000, "198.51.100.45", true)]
    [InlineData(0, 0, "", false)]
    [InlineData(52000, 52500, "", true)]
    public async Task ReconfigurationPreservesConfiguredNetworkChoicesAndInitializesOnlyUnsetValues(
        int low, int high, string address, bool wrapped)
    {
        var result = await Run(low, high, address, wrapped);
        Assert.True(result.Success, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(low == 0 ? 50000 : low, json.RootElement.GetProperty("low").GetInt32());
        Assert.Equal(high == 0 ? 51000 : high, json.RootElement.GetProperty("high").GetInt32());
        Assert.Equal(address.Length == 0 ? "192.0.2.30" : address, json.RootElement.GetProperty("address").GetString());
        Assert.Equal(low == 0 ? 2 : 0, json.RootElement.GetProperty("rangeWrites").GetInt32());
        Assert.Equal(address.Length == 0 ? 1 : 0, json.RootElement.GetProperty("addressWrites").GetInt32());
    }

    [Theory]
    [InlineData(0, 52500)]
    [InlineData(52000, 51000)]
    [InlineData(52000, 65536)]
    public async Task InvalidExistingRangeStopsWithoutReplacingTheAdministratorsSettings(int low, int high)
    {
        var result = await Run(low, high, "198.51.100.45", false);
        Assert.False(result.Success);
        Assert.Contains("existing settings were preserved", result.StandardError, StringComparison.Ordinal);
    }

    // Every relevant cmdlet is replaced in this child process. The generated production
    // fragment cannot reach IIS, a real service, firewall or local Windows account.
    private static Task<CommandResult> Run(int low, int high, string address, bool wrapped)
    {
        var fake = $$"""
            $ErrorActionPreference = 'Stop'
            $siteName = 'AdaPanel FTP'
            $defaultPassiveLow = 50000
            $defaultPassiveHigh = 51000
            $defaultExternalIp = '192.0.2.30'
            $script:network = @{ lowDataChannelPort = {{low}}; highDataChannelPort = {{high}}; address = '{{address}}'; rangeWrites = 0; addressWrites = 0 }
            function Get-WebConfigurationProperty { [CmdletBinding()]param($PSPath,$Filter,$Name) [pscustomobject]@{ Value = $script:network[$Name] } }
            function Set-WebConfigurationProperty { [CmdletBinding()]param($PSPath,$Filter,$Name,$Value) $script:network[$Name] = $Value; $script:network.rangeWrites++ }
            function Get-ItemProperty { [CmdletBinding()]param($Path,$Name)
              if ({{(wrapped ? "$true" : "$false")}}) { return [pscustomobject]@{ Value = $script:network.address } }
              return $script:network.address
            }
            function Set-ItemProperty { [CmdletBinding()]param($Path,$Name,$Value) $script:network.address = $Value; $script:network.addressWrites++ }
            """;
        return ProcessRunner.RunPowerShellAsync(fake + Environment.NewLine + FtpService.FtpNetworkSettingsScript + Environment.NewLine + "[pscustomobject]@{ low = $passiveLow; high = $passiveHigh; address = $script:network.address; rangeWrites = $script:network.rangeWrites; addressWrites = $script:network.addressWrites } | ConvertTo-Json -Compress", CancellationToken.None);
    }
}
