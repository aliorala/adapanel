using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;

namespace AdaPanel.Infrastructure.Ftp;

public sealed partial class FtpService
{
    private sealed class ExternalAddressMeasurement
    {
        public bool Exists { get; set; }
        public bool Owned { get; set; }
        public string Address { get; set; } = string.Empty;
    }

    public Task<FtpServerAddressSyncDto> SyncServerAddressAsync(string oldAddress, string newAddress,
        CancellationToken cancellationToken = default) => InfrastructureLock.RunExclusiveAsync(InfrastructureLockKey,
        () => SyncExternalAddressCoreAsync(oldAddress, newAddress, cancellationToken), cancellationToken);

    private async Task<FtpServerAddressSyncDto> SyncExternalAddressCoreAsync(string oldAddress, string newAddress, CancellationToken token)
    {
        var result = new FtpServerAddressSyncDto();
        try
        {
            if (!IsCanonicalIpv4(oldAddress) || !IsCanonicalIpv4(newAddress) || oldAddress == newAddress)
                throw new InvalidOperationException("FTP migration requires two distinct IPv4 addresses.");
            var before = await ReadExternalAddressAsync(token);
            result.CurrentAddress = before.Exists && before.Owned ? before.Address : null;
            if (!before.Exists || !before.Owned)
            {
                result.Skipped = true;
                result.Error = before.Exists ? "The FTP site could not be verified as panel-owned; it was preserved." : "The panel FTP site is not installed.";
                return result;
            }
            if (before.Address == newAddress) { result.Succeeded = true; return result; }
            if (before.Address != oldAddress)
            {
                result.Skipped = true;
                result.Error = "The custom or empty FTP external address was preserved.";
                return result;
            }
            await fileStore.WriteAsync($"config/ftp-address-backups/{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json",
                new { siteName = FtpSiteName, previousAddress = before.Address, requestedAddress = newAddress, createdUtc = DateTime.UtcNow }, token);
            var script = BuildExternalAddressContext() + $$"""

                # ADAPANEL-FTP-ADDRESS-WRITE
                try {
                  if (-not $adaOwned) { throw 'The panel FTP site ownership changed before publication.' }
                  $firewall = $adaSite.GetChildElement('ftpServer').GetChildElement('firewallSupport')
                  if ([string]$firewall.GetAttributeValue('externalIp4Address') -cne '{{EscapePowerShellLiteral(oldAddress)}}') {
                    throw 'The FTP external address changed concurrently; it was preserved.'
                  }
                  $firewall.SetAttributeValue('externalIp4Address', '{{EscapePowerShellLiteral(newAddress)}}')
                  $adaManager.CommitChanges()
                } finally { if ($null -ne $adaManager) { $adaManager.Dispose() } }
                """;
            // Once publication starts, the earlier observation no longer proves the current
            // value. A failed command may have committed before returning an error.
            result.CurrentAddress = null;
            var write = await RunStrictScriptAsync(script, token);
            if (!write.Success) throw new InvalidOperationException("FTP external address publication failed; retry will remeasure the actual configuration.");
            result.Changed = true;
            var after = await ReadExternalAddressAsync(token);
            result.CurrentAddress = after.Exists && after.Owned ? after.Address : null;
            if (!after.Exists || !after.Owned || after.Address != newAddress)
                throw new InvalidOperationException("The new FTP external address was not confirmed by native readback.");
            result.Succeeded = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var error = SecretRedactor.Redact(exception.Message);
            result.Error = error.Length <= 1200 ? error : error[..1200];
        }
        return result;
    }

    private async Task<ExternalAddressMeasurement> ReadExternalAddressAsync(CancellationToken token)
    {
        var script = BuildExternalAddressContext() + """

            # ADAPANEL-FTP-ADDRESS-READ
            try {
              $address = ''; if ($adaOwned) { $address = [string]$adaSite.GetChildElement('ftpServer').GetChildElement('firewallSupport').GetAttributeValue('externalIp4Address') }
              [pscustomobject]@{ Exists=($null -ne $adaSite); Owned=[bool]$adaOwned; Address=$address } | ConvertTo-Json -Compress
            } finally { if ($null -ne $adaManager) { $adaManager.Dispose() } }
            """;
        var response = await RunStrictScriptAsync(script, token);
        if (!response.Success) throw new InvalidOperationException("The FTP external address could not be measured.");
        try
        {
            using var document = JsonDocument.Parse(response.StandardOutput);
            if (!document.RootElement.TryGetProperty("Exists", out _) || !document.RootElement.TryGetProperty("Owned", out _) ||
                !document.RootElement.TryGetProperty("Address", out _)) throw new JsonException();
            return JsonSerializer.Deserialize<ExternalAddressMeasurement>(response.StandardOutput) ?? throw new JsonException();
        }
        catch (JsonException) { throw new InvalidOperationException("The FTP external address measurement was incomplete."); }
    }

    private string BuildExternalAddressContext() => $$"""
        $ErrorActionPreference='Stop'
        $adaManager=$null; $adaSite=$null; $adaOwned=$false
        $adaMwa=Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
        if (Test-Path -LiteralPath $adaMwa) {
          Add-Type -Path $adaMwa -ErrorAction Stop
          $adaManager=New-Object Microsoft.Web.Administration.ServerManager -ErrorAction Stop
          $adaMatches=@($adaManager.Sites | Where-Object { $_.Name -ceq '{{EscapePowerShellLiteral(FtpSiteName)}}' })
          if ($adaMatches.Count -gt 1) { $adaManager.Dispose(); throw 'Ambiguous panel FTP site.' }
          if ($adaMatches.Count -eq 1) {
            $adaSite=$adaMatches[0]
            $adaRoot=[Environment]::ExpandEnvironmentVariables([string]$adaSite.Applications['/'].VirtualDirectories['/'].PhysicalPath)
            $adaExpected='{{EscapePowerShellLiteral(Path.Combine(dataRoot, "ftp-root"))}}'
            $adaOwned=([IO.Path]::GetFullPath($adaRoot).TrimEnd('\') -ieq [IO.Path]::GetFullPath($adaExpected).TrimEnd('\')) -and @($adaSite.Bindings | Where-Object { $_.Protocol -ieq 'ftp' }).Count -gt 0
          }
        }
        """;

    private static bool IsCanonicalIpv4(string value) => IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == AddressFamily.InterNetwork && address.ToString() == value;
}
