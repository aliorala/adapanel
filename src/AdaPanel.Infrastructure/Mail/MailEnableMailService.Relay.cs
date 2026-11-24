using System.Net;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailEnableMailService
{
    // MailEnable 10.61's SMTP binary reads these values from the 32-bit registry view.
    // They are not exposed by the vendor PowerShell provisioning settings catalogue.
    internal const string RelayRegistryHeader = """
        function Open-RelayRegistry([bool]$writable) {
          $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry32)
          try { $key = $base.OpenSubKey('SOFTWARE\Mail Enable\Mail Enable\Connectors\SMTP', $writable) } finally { $base.Dispose() }
          if ($null -eq $key) { throw 'MailEnable SMTP settings unavailable.' }
          return $key
        }
        function Read-RelayDword([string]$name) {
          $key = Open-RelayRegistry $false
          try {
            if ($key.GetValueNames() -notcontains $name) { return 0 }
            if ($key.GetValueKind($name) -ne [Microsoft.Win32.RegistryValueKind]::DWord) { throw 'Invalid SMTP setting type.' }
            return [int]$key.GetValue($name)
          } finally { $key.Dispose() }
        }
        """;

    internal static readonly string[] RelaySettings = ["smtpForwardAllOutboundEnabled", "smtpForwardAllOutboundHost",
        "smtpOutboundAuthenticationStatus", "smtpOutboundUsername", "smtpOutboundPassword", "smtpOutboundTLSMode", "smtpNoTLSFallback"];

    internal const string RelayWriteHeader = """
        function Write-RelaySetting([string]$name, [string]$value) {
          # Set-MailEnablePlatform's mandatory Value parameter rejects empty strings. Use the
          # same vendor API so disabling a relay can actually clear its stored credentials.
          $control = [MailEnable.Administration.Settings]::GetControl($name, $null)
          if ($null -eq $control) { throw 'Unknown SMTP setting.' }
          $control.value = $value
          if (-not $control.SetValue($null)) { throw 'SMTP setting could not be saved.' }
        }
        """;

    internal const string RelayRecoveryHeader = """
        function Relay-JournalPath {
          $directory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AdaPanel\Recovery\MailEnableRelay'
          if (Test-Path -LiteralPath $directory) {
            if (([IO.DirectoryInfo]$directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe relay recovery directory.' }
          } else { [IO.Directory]::CreateDirectory($directory) | Out-Null }
          $acl = New-Object Security.AccessControl.DirectorySecurity
          $acl.SetAccessRuleProtection($true, $false)
          foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
          }
          [IO.Directory]::SetAccessControl($directory, $acl)
          return (Join-Path $directory 'pending.dpapi')
        }
        function Save-RelayJournal($settings, $registry) {
          Add-Type -AssemblyName System.Security -ErrorAction Stop
          $path = Relay-JournalPath
          if (Test-Path -LiteralPath $path) { throw 'An earlier relay transaction needs recovery.' }
          $data = [Text.Encoding]::UTF8.GetBytes((@{ settings=$settings; registry=$registry } | ConvertTo-Json -Depth 5 -Compress))
          try { $encrypted = [Security.Cryptography.ProtectedData]::Protect($data, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine) }
          finally { [Array]::Clear($data, 0, $data.Length) }
          $temporary = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
          try { [IO.File]::WriteAllBytes($temporary, $encrypted); [IO.File]::Move($temporary, $path) }
          finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
        }
        function Clear-RelayJournal {
          $path = Relay-JournalPath
          if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
        }
        function Restore-RelayJournal {
          $path = Relay-JournalPath
          if (-not (Test-Path -LiteralPath $path)) { return }
          if (([IO.FileInfo]$path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe relay recovery file.' }
          Add-Type -AssemblyName System.Security -ErrorAction Stop
          $data = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($path), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
          try { $saved = [Text.Encoding]::UTF8.GetString($data) | ConvertFrom-Json }
          finally { [Array]::Clear($data, 0, $data.Length) }
          $expected = @('smtpForwardAllOutboundEnabled','smtpForwardAllOutboundHost','smtpOutboundAuthenticationStatus','smtpOutboundUsername','smtpOutboundPassword','smtpOutboundTLSMode','smtpNoTLSFallback')
          if (@($saved.settings.PSObject.Properties).Count -ne $expected.Count) { throw 'Invalid relay recovery data.' }
          foreach ($name in $expected) { if ($null -eq $saved.settings.PSObject.Properties[$name]) { throw 'Incomplete relay recovery data.' } }
          $service = Get-Service -Name MESMTPC -ErrorAction Stop
          if ($service.Status -ne 'Stopped') { $service.Stop(); $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(5)) }
          foreach ($name in $expected) { Write-RelaySetting $name ([string]$saved.settings.$name) }
          $key = Open-RelayRegistry $true
          try {
            foreach ($name in @('Forward All Outbound SSL','Validate Outbound TLS Cert')) {
              $entry = $saved.registry.$name
              if ($null -eq $entry -or $entry.exists -isnot [bool] -or $entry.value -notin @(0,1)) { throw 'Invalid relay security recovery data.' }
              if ($entry.exists) { $key.SetValue($name, [int]$entry.value, [Microsoft.Win32.RegistryValueKind]::DWord) } else { $key.DeleteValue($name, $false) }
              if (($key.GetValueNames() -contains $name) -ne $entry.exists -or (Read-RelayDword $name) -ne $entry.value) { throw 'Relay security restoration failed.' }
            }
          } finally { $key.Dispose() }
          foreach ($name in $expected) { if ((Read-Setting $name) -cne [string]$saved.settings.$name) { throw 'Relay restoration read-back failed.' } }
          $service.Start()
          $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(5))
          Clear-RelayJournal
        }
        """;

    public async Task<MailRelayDto> GetRelayAsync(CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        return await ReadRelayAsync(cancellationToken);
    }

    private async Task<MailRelayDto> ReadRelayAsync(CancellationToken cancellationToken)
    {
        var assignments = string.Join("\n", RelaySettings.Where(k => k != "smtpOutboundPassword")
            .Select(k => $"$data['{k}'] = Read-Setting '{k}'"));
        var script = PlatformHeader + "\n" + RelayRegistryHeader + "\n" + """
            # Read the journal marker before interpreting any partially written native values.
            if (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AdaPanel\Recovery\MailEnableRelay\pending.dpapi')) {
              @{ recoveryPending = 'True' } | ConvertTo-Json -Compress
              return
            }
            $data = @{}
            """ + "\n" + assignments + "\n" + """
            $data['hasPassword'] = (-not [string]::IsNullOrEmpty((Read-Setting 'smtpOutboundPassword'))).ToString()
            $data['implicitTls'] = (Read-RelayDword 'Forward All Outbound SSL').ToString()
            $data['validateCertificate'] = (Read-RelayDword 'Validate Outbound TLS Cert').ToString()
            $data['recoveryPending'] = (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AdaPanel\Recovery\MailEnableRelay\pending.dpapi')).ToString()
            $data | ConvertTo-Json -Compress
            """;
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(await RunComAsync(script, cancellationToken), JsonOptions);
            return ParseMailEnableRelay(data ?? throw new JsonException());
        }
        catch (JsonException) { throw new AppException("mail.settings_unmeasured", 502, 502); }
    }

    internal static MailRelayDto ParseMailEnableRelay(IReadOnlyDictionary<string, string> data)
    {
        // A crashed transaction can leave any setting partially written. Its journal
        // must remain recoverable even when the intermediate configuration is invalid.
        if (data.GetValueOrDefault("recoveryPending") == "True")
            return new MailRelayDto { Installed = true, Configured = false, SecurityStatus = "recovery-pending" };
        bool Flag(string key)
        {
            var value = data.GetValueOrDefault(key);
            if (value is not ("0" or "1")) throw new AppException("mail.settings_unmeasured", 502, 502);
            return value == "1";
        }
        var enabled = Flag("smtpForwardAllOutboundEnabled");
        var implicitTls = Flag("implicitTls");
        var validate = Flag("validateCertificate");
        var tls = Flag("smtpOutboundTLSMode");
        var noFallback = Flag("smtpNoTLSFallback");
        var authentication = Flag("smtpOutboundAuthenticationStatus");
        if (!bool.TryParse(data.GetValueOrDefault("hasPassword"), out var hasPassword))
            throw new AppException("mail.settings_unmeasured", 502, 502);
        var endpoint = data.GetValueOrDefault("smtpForwardAllOutboundHost", "");
        var host = endpoint;
        var port = 25;
        if (endpoint.Length > 0)
        {
            var colon = endpoint.LastIndexOf(':');
            if (colon > 0 && endpoint.IndexOf(':') == colon && int.TryParse(endpoint[(colon + 1)..], out var configuredPort))
            { host = endpoint[..colon]; port = configuredPort; }
            if (!ValidRelayHost(host) || port is < 1 or > 65535)
                throw new AppException("mail.relay_unmeasured_route", 409, 409);
        }
        if (enabled && host.Length == 0) throw new AppException("mail.settings_unmeasured", 502, 502);
        return new MailRelayDto
        {
            Installed = true, Configured = true, Enabled = enabled, Host = host, Port = port, Protocol = "smtp",
            ImplicitTls = implicitTls, AllowInvalidCerts = implicitTls || !validate,
            Username = authentication ? data.GetValueOrDefault("smtpOutboundUsername", "") : "", HasPassword = hasPassword,
            SecurityStatus = data.GetValueOrDefault("recoveryPending") == "True" ? "recovery-pending"
                : !enabled ? "disabled" : implicitTls ? "native-implicit-tls-unverified"
                : tls && validate && noFallback ? "verified-settings" : "insecure-settings"
        };
    }

    public async Task<MailRelayDto> SetRelayAsync(SetMailRelayRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.RecoverOnly) ValidateMailEnableRelay(request);
        using var mutation = await AcquireMutationAsync(cancellationToken);
        var before = await ReadRelayAsync(cancellationToken);
        if (before.SecurityStatus == "recovery-pending")
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunVerifiedCommandAsync(PlatformHeader + "\n" + RelayRegistryHeader + "\n" + RelayWriteHeader + "\n"
                + RelayRecoveryHeader + "\nRestore-RelayJournal\n'OK'", CancellationToken.None);
            before = await ReadRelayAsync(cancellationToken);
        }
        if (request.RecoverOnly) return before;
        if (request.Enabled)
        {
            if (string.IsNullOrEmpty(request.Password) && request.Username.Length > 0
                && (!before.HasPassword || !string.Equals(before.Host, request.Host.Trim(), StringComparison.OrdinalIgnoreCase)
                    || before.Username != request.Username))
                throw new AppException("mail.relay_password_required", 400, 400);
            var probe = await ClientProbe(request.Host.Trim(), request.Port, "smtp", true, cancellationToken);
            if (probe.Status != "ok") throw new AppException("mail.relay_tls_failed", 502, 502);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Once the transaction starts, finish restoration even if the HTTP caller disconnects.
        var result = (await RunComAsync(RelayMutationScript(request), CancellationToken.None)).Trim();
        if (result == "ROLLBACK_FAILED") throw new AppException("mail.relay_rollback_failed", 502, 502);
        if (result != "OK") throw new AppException("mail.relay_failed", 502, 502);
        var after = await ReadRelayAsync(CancellationToken.None);
        if (after.Enabled != request.Enabled || (request.Enabled &&
            (!after.Host.Equals(request.Host.Trim(), StringComparison.OrdinalIgnoreCase) || after.Port != request.Port
                || after.Username != request.Username || after.SecurityStatus != "verified-settings")))
            throw new AppException("mail.relay_unverified", 502, 502);
        return after;
    }

    internal static bool ValidRelayHost(string host) => host.Length is > 0 and <= 253
        && !host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "/\\;,:[]@*?".Contains(c))
        && (Uri.CheckHostName(host) == UriHostNameType.Dns || IPAddress.TryParse(host, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

    internal static void ValidateMailEnableRelay(SetMailRelayRequest request)
    {
        if (!request.Enabled) return;
        if (string.IsNullOrWhiteSpace(request.Host) || !ValidRelayHost(request.Host.Trim()) || request.Port is < 1 or > 65535
            || request.Username is null || request.Username.Length > 255 || request.Username.Any(char.IsControl)
            || (request.Password?.Length ?? 0) > 512 || (request.Password?.Contains('\0') ?? false))
            throw new AppException("mail.relay_invalid", 400, 400);
        if (!string.Equals(request.Protocol, "smtp", StringComparison.OrdinalIgnoreCase))
            throw new AppException("mail.relay_smtp_required", 400, 400);
        if (request.AllowInvalidCerts) throw new AppException("mail.relay_certificate_validation_required", 400, 400);
        // The native 10.61 implicit-TLS call ignores Validate Outbound TLS Cert. Do not pretend
        // a successful one-off probe can repair that ongoing engine-level validation gap.
        if (request.ImplicitTls) throw new AppException("mail.relay_implicit_tls_unverified", 400, 400);
        if (request.Host.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(request.Host.Trim(), out var address) && IPAddress.IsLoopback(address))
            throw new AppException("mail.relay_invalid", 400, 400);
    }

    internal static string RelayMutationScript(SetMailRelayRequest request)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            enabled = request.Enabled, host = request.Host?.Trim() ?? "", port = request.Port,
            username = request.Username ?? "", password = request.Password ?? ""
        })));
        var settings = string.Join(",", RelaySettings.Select(k => "'" + k + "'"));
        return PlatformHeader + "\n" + RelayRegistryHeader + "\n" + RelayWriteHeader + "\n" + RelayRecoveryHeader + "\n" + $$"""
            Restore-RelayJournal
            $request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{payload}}')) | ConvertFrom-Json
            $names = @({{settings}})
            $snapshot = @{}
            foreach ($name in $names) { $snapshot[$name] = Read-Setting $name }
            $desired = @{}
            foreach ($name in $names) { $desired[$name] = $snapshot[$name] }
            $desired['smtpForwardAllOutboundEnabled'] = if ($request.enabled) { '1' } else { '0' }
            $desired['smtpForwardAllOutboundHost'] = if ($request.enabled) { $request.host + ':' + $request.port } else { '' }
            $desired['smtpOutboundUsername'] = if ($request.enabled) { [string]$request.username } else { '' }
            $desired['smtpOutboundAuthenticationStatus'] = if ($request.enabled -and $request.username.Length -gt 0) { '1' } else { '0' }
            $desired['smtpOutboundPassword'] = if (-not $request.enabled -or $request.username.Length -eq 0) { '' }
              elseif ($request.password.Length -gt 0) { [string]$request.password } else { $snapshot['smtpOutboundPassword'] }
            if ($request.enabled) {
              if ($request.username.Length -gt 0 -and $desired['smtpOutboundPassword'].Length -eq 0) { throw 'Relay credentials required.' }
              if ($request.username.Length -gt 0 -and $request.password.Length -eq 0 -and
                  ($snapshot['smtpOutboundUsername'] -cne $request.username -or
                   $snapshot['smtpForwardAllOutboundHost'] -ine $desired['smtpForwardAllOutboundHost'])) { throw 'New relay credentials required.' }
              $desired['smtpOutboundTLSMode'] = '1'
              $desired['smtpNoTLSFallback'] = '1'
            }
            $registryNames = @('Forward All Outbound SSL', 'Validate Outbound TLS Cert')
            $registrySnapshot = @{}
            $registryDesired = @{}
            $key = Open-RelayRegistry $true
            try {
              foreach ($name in $registryNames) {
                $exists = $key.GetValueNames() -contains $name
                if ($exists -and $key.GetValueKind($name) -ne [Microsoft.Win32.RegistryValueKind]::DWord) { throw 'Invalid SMTP setting type.' }
                $value = if ($exists) { [int]$key.GetValue($name) } else { 0 }
                if ($value -notin @(0,1)) { throw 'Unknown SMTP security mode.' }
                $registrySnapshot[$name] = @{ exists=$exists; value=$value }
                $registryDesired[$name] = $value
              }
              if ($request.enabled) {
                $registryDesired['Forward All Outbound SSL'] = 0
                $registryDesired['Validate Outbound TLS Cert'] = 1
              }
              $changed = @($names | Where-Object { $snapshot[$_] -cne $desired[$_] })
              $registryChanged = @($registryNames | Where-Object { $registrySnapshot[$_].value -ne $registryDesired[$_] })
              $service = Get-Service -Name MESMTPC -ErrorAction Stop
              if ($service.Status -ne 'Running') { throw 'MailEnable SMTP must be running to activate and verify relay settings.' }
              if ($changed.Count -eq 0 -and $registryChanged.Count -eq 0) { 'OK'; return }
              Save-RelayJournal $snapshot $registrySnapshot
              try {
                $service.Stop()
                $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(5))
                foreach ($name in $changed) { Write-RelaySetting $name $desired[$name] }
                foreach ($name in $registryChanged) { $key.SetValue($name, $registryDesired[$name], [Microsoft.Win32.RegistryValueKind]::DWord) }
                foreach ($name in $changed) { if ((Read-Setting $name) -cne $desired[$name]) { throw 'Relay setting read-back failed.' } }
                foreach ($name in $registryChanged) { if ((Read-RelayDword $name) -ne $registryDesired[$name]) { throw 'Relay security read-back failed.' } }
                $service.Start()
                $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(5))
                Clear-RelayJournal
                'OK'
              } catch {
                $restored = $true
                try {
                  Restore-RelayJournal
                } catch { $restored = $false }
                if ($restored) { 'FAILED' } else { 'ROLLBACK_FAILED' }
              }
            } finally { $key.Dispose() }
            """;
    }
}
