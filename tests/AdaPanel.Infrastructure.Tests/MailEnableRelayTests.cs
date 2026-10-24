using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailEnableRelayTests
{
    private static Dictionary<string, string> Snapshot() => new()
    {
        ["smtpForwardAllOutboundEnabled"] = "1", ["smtpForwardAllOutboundHost"] = "relay.example.test:587",
        ["smtpOutboundAuthenticationStatus"] = "1", ["smtpOutboundUsername"] = "account",
        ["smtpOutboundTLSMode"] = "1", ["smtpNoTLSFallback"] = "1", ["hasPassword"] = "True",
        ["implicitTls"] = "0", ["validateCertificate"] = "1"
    };

    [Fact]
    public void Existing_insecure_native_relay_is_visible_and_editable_not_reported_secure()
    {
        var values = Snapshot();
        values["implicitTls"] = "1";
        var dto = MailEnableMailService.ParseMailEnableRelay(values);
        Assert.True(dto.Configured);
        Assert.True(dto.Enabled);
        Assert.True(dto.AllowInvalidCerts);
        Assert.Equal("native-implicit-tls-unverified", dto.SecurityStatus);
    }

    [Fact]
    public async Task Reading_pending_recovery_reports_it_without_any_service_or_file_mutation()
    {
        var values = Snapshot();
        values["recoveryPending"] = "True";
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = (script, _) =>
            {
                Assert.DoesNotContain("Restore-RelayJournal", script);
                Assert.DoesNotContain("SetAccessControl", script);
                Assert.DoesNotContain("Start-Service", script);
                Assert.DoesNotContain(".Start()", script);
                return Task.FromResult(JsonSerializer.Serialize(values));
            }
        };
        var result = await service.GetRelayAsync();
        Assert.False(result.Configured);
        Assert.Equal("recovery-pending", result.SecurityStatus);
    }

    [Fact]
    public async Task Pending_journal_is_read_before_corrupt_native_values_and_GET_does_not_restore()
    {
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = async (script, token) => {
                script = script.Replace(MailEnableMailService.PlatformHeader, "function Read-Setting { throw 'Corrupt native setting was read' }")
                    .Replace(MailEnableMailService.RelayRegistryHeader, "function Read-RelayDword { throw 'Corrupt registry setting was read' }");
                var result = await ProcessRunner.RunPowerShellAsync("function Test-Path { param($LiteralPath) return $true }\n" + script, token);
                Assert.True(result.Success, result.StandardError);
                Assert.DoesNotContain("Restore-RelayJournal", script);
                return result.StandardOutput;
            }
        };
        var result = await service.GetRelayAsync();
        Assert.Equal("recovery-pending", result.SecurityStatus);
        Assert.False(result.Configured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_crash_settings_remain_visible_and_SET_restores_before_parsing(bool corruptFlags)
    {
        var pending = Snapshot();
        pending["recoveryPending"] = "True";
        pending["smtpForwardAllOutboundHost"] = "";
        if (corruptFlags) pending["smtpOutboundTLSMode"] = "partial";
        var disabled = Snapshot();
        disabled["smtpForwardAllOutboundEnabled"] = "0";
        disabled["smtpForwardAllOutboundHost"] = "";
        var calls = 0;
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = (script, _) =>
            {
                calls++;
                if (calls is 1 or 2) {
                    Assert.DoesNotContain("Restore-RelayJournal", script);
                    return Task.FromResult(JsonSerializer.Serialize(pending));
                }
                if (calls == 3) {
                    Assert.Contains("Restore-RelayJournal\n'OK'", script);
                    return Task.FromResult("OK");
                }
                if (calls == 5) {
                    Assert.Contains("Save-RelayJournal", script);
                    return Task.FromResult("OK");
                }
                return Task.FromResult(JsonSerializer.Serialize(disabled));
            }
        };
        var status = await service.GetRelayAsync();
        Assert.Equal("recovery-pending", status.SecurityStatus);
        Assert.False(status.Configured);
        Assert.False(status.HasPassword);
        Assert.Empty(status.Host);
        var result = await service.SetRelayAsync(new() { Enabled = false });
        Assert.Equal("disabled", result.SecurityStatus);
        Assert.Equal(6, calls);
        pending.Remove("recoveryPending");
        Assert.Throws<AppException>(() => MailEnableMailService.ParseMailEnableRelay(pending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recover_only_restores_previous_enabled_settings_without_applying_disabled_request(bool pending)
    {
        var previous = Snapshot();
        var crashed = new Dictionary<string, string> { ["recoveryPending"]="True", ["smtpForwardAllOutboundEnabled"]="partial" };
        var calls = 0;
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = (script, _) => {
                calls++;
                Assert.DoesNotContain("Save-RelayJournal $snapshot", script);
                if (pending && calls == 1) return Task.FromResult(JsonSerializer.Serialize(crashed));
                if (pending && calls == 2) {
                    Assert.Contains("Restore-RelayJournal\n'OK'", script);
                    return Task.FromResult("OK");
                }
                Assert.DoesNotContain("Restore-RelayJournal", script);
                return Task.FromResult(JsonSerializer.Serialize(previous));
            },
            ClientProbe = (_, _, _, _, _) => throw new InvalidOperationException("Recovery must not probe new settings")
        };
        var restored = await service.SetRelayAsync(new() { RecoverOnly=true, Enabled=false });
        Assert.True(restored.Enabled);
        Assert.True(restored.HasPassword);
        Assert.Equal("relay.example.test", restored.Host);
        Assert.Equal(pending ? 3 : 1, calls);
    }

    [Fact]
    public async Task Stalwart_rejects_recover_only_before_accessing_runtime_or_mutating_settings()
    {
        var service = new MailService(null!, null!, null!, null!, null!, null!, null!, NullLogger<MailService>.Instance);
        var error = await Assert.ThrowsAsync<AppException>(() => service.SetRelayAsync(new() { RecoverOnly=true, Enabled=false }));
        Assert.Equal("mail.relay_recovery_unsupported", error.MessageKey);
    }

    [Fact]
    public void Null_JSON_fields_and_whitespace_loopback_are_rejected_as_validation_errors()
    {
        Assert.Throws<AppException>(() => MailEnableMailService.ValidateMailEnableRelay(new() { Enabled = true, Host = null! }));
        Assert.Throws<AppException>(() => MailEnableMailService.ValidateMailEnableRelay(new() { Enabled = true, Host = "relay.test", Username = null! }));
        Assert.Throws<AppException>(() => MailEnableMailService.ValidateMailEnableRelay(new() { Enabled = true, Host = " localhost " }));
        Assert.Throws<AppException>(() => MailEnableMailService.ValidateMailEnableRelay(new() { Enabled = true, Host = " 127.0.0.1 " }));
    }

    [Theory]
    [InlineData("smtpOutboundTLSMode")]
    [InlineData("smtpNoTLSFallback")]
    [InlineData("validateCertificate")]
    public void All_three_security_controls_are_required_to_report_verified_settings(string key)
    {
        var values = Snapshot();
        values[key] = "0";
        Assert.Equal("insecure-settings", MailEnableMailService.ParseMailEnableRelay(values).SecurityStatus);
        values[key] = "unknown";
        Assert.Throws<AppException>(() => MailEnableMailService.ParseMailEnableRelay(values));
    }

    [Theory]
    [InlineData("relay.test;neighbor.test")]
    [InlineData("https://relay.test")]
    [InlineData("relay.test:587")]
    [InlineData("relay.test\r\n")]
    [InlineData("relay.test/other")]
    public void Host_route_injection_is_rejected(string host)
        => Assert.False(MailEnableMailService.ValidRelayHost(host));

    [Fact]
    public async Task Unsupported_transport_and_disabled_validation_fail_before_any_settings_read()
    {
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        { KomutKosucusu = (_, _) => throw new InvalidOperationException("No native command expected.") };
        await Assert.ThrowsAsync<AppException>(() => service.SetRelayAsync(new() { Enabled = true, Host = "relay.test", ImplicitTls = true }));
        await Assert.ThrowsAsync<AppException>(() => service.SetRelayAsync(new() { Enabled = true, Host = "relay.test", AllowInvalidCerts = true }));
        await Assert.ThrowsAsync<AppException>(() => service.SetRelayAsync(new() { Enabled = true, Host = "relay.test", Protocol = "lmtp" }));
    }

    [Fact]
    public async Task Failed_real_TLS_probe_prevents_any_write()
    {
        var calls = 0;
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = (_, _) => { calls++; return Task.FromResult(JsonSerializer.Serialize(Snapshot())); },
            ClientProbe = (_, _, _, _, _) => Task.FromResult(new MailTlsEndpointDto { Status = "fail" })
        };
        await Assert.ThrowsAsync<AppException>(() => service.SetRelayAsync(new()
        { Enabled = true, Host = "relay.example.test", Port = 587, Username = "account" }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Preserved_password_cannot_be_sent_to_a_new_relay_or_account()
    {
        var calls = 0;
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = (_, _) => { calls++; return Task.FromResult(JsonSerializer.Serialize(Snapshot())); },
            ClientProbe = (_, _, _, _, _) => throw new InvalidOperationException("Credentials should be rejected first.")
        };
        await Assert.ThrowsAsync<AppException>(() => service.SetRelayAsync(new()
        { Enabled = true, Host = "new-relay.example.test", Username = "account" }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Interrupted_transaction_recovers_from_DPAPI_journal_in_a_new_process_without_plaintext_password()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adapanel-relay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pending.dpapi");
        var overridePath = "\nfunction Relay-JournalPath { return '" + path.Replace("'", "''") + "' }\n";
        try
        {
            var common = MockPlatform + "\n" + MockRegistry + "\n" + MockWriter + "\n" + MailEnableMailService.RelayRecoveryHeader + overridePath;
            var save = await ProcessRunner.RunPowerShellAsync(common + """
                $snapshot=@{}; foreach($name in @('smtpForwardAllOutboundEnabled','smtpForwardAllOutboundHost','smtpOutboundAuthenticationStatus','smtpOutboundUsername','smtpOutboundPassword','smtpOutboundTLSMode','smtpNoTLSFallback')) { $snapshot[$name]=$script:settings[$name] }
                Save-RelayJournal $snapshot @{'Forward All Outbound SSL'=@{exists=$false;value=0};'Validate Outbound TLS Cert'=@{exists=$true;value=1}}
                """, default);
            Assert.True(save.Success, save.StandardError);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain("sensitive-test-password", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
            var restore = await ProcessRunner.RunPowerShellAsync(common + """
                $script:settings['smtpOutboundPassword']='partial-update'
                $script:registry['Forward All Outbound SSL']=1
                $script:service.Status='Stopped'
                Restore-RelayJournal
                if ($script:settings['smtpOutboundPassword'] -cne 'sensitive-test-password') { throw 'Password was not restored' }
                if ($script:registry.ContainsKey('Forward All Outbound SSL')) { throw 'Absent registry setting was not restored' }
                if ($script:service.Status -ne 'Running') { throw 'SMTP was not restored' }
                'OK'
                """, default);
            Assert.True(restore.Success, restore.StandardError);
            Assert.Equal("OK", restore.StandardOutput.Trim());
            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private const string MockPlatform = """
        $ErrorActionPreference='Stop'
        $script:settings = @{
          smtpForwardAllOutboundEnabled='1'; smtpForwardAllOutboundHost='relay.example.test:587'
          smtpOutboundAuthenticationStatus='1'; smtpOutboundUsername='account'; smtpOutboundPassword='sensitive-test-password'
          smtpOutboundTLSMode='1'; smtpNoTLSFallback='1'; smtpSmartHostProcessingMode='1'
          tenantRoute='neighbor.example.test:2525'
        }
        $script:before = @{}
        foreach ($name in $script:settings.Keys) { $script:before[$name]=$script:settings[$name] }
        $script:registry = @{'Forward All Outbound SSL'=0; 'Validate Outbound TLS Cert'=1}
        $script:writes=0; $script:stops=0; $script:starts=0
        $script:service=[pscustomobject]@{ Status='Running' }
        $script:service | Add-Member ScriptMethod WaitForStatus { param($status,$timeout) if ($this.Status -ne $status) { throw 'Bad service state' } }
        $script:service | Add-Member ScriptMethod Refresh { }
        $script:service | Add-Member ScriptMethod Stop { Stop-Service -Name MESMTPC }
        $script:service | Add-Member ScriptMethod Start { Start-Service -Name MESMTPC }
        function Get-Service { param($Name,$ErrorAction) if ($Name -ne 'MESMTPC') { throw 'Unexpected service' }; return $script:service }
        function Stop-Service { param($Name,$ErrorAction) if ($Name -ne 'MESMTPC') { throw 'Unexpected service' }; $script:service.Status='Stopped'; $script:stops++ }
        function Start-Service {
          param($Name,$ErrorAction)
          if ($Name -ne 'MESMTPC') { throw 'Unexpected service' }
          $script:starts++
          if ($script:failStart) { $script:failStart=$false; throw 'Activation failed' }
          $script:service.Status='Running'
        }
        function Read-Setting { param($name) if (-not $script:settings.ContainsKey($name)) { throw 'Unknown setting' }; return [string]$script:settings[$name] }
        """;

    private const string MockRegistry = """
        function Open-RelayRegistry {
          param($writable)
          $o=[pscustomobject]@{}
          $o | Add-Member ScriptMethod GetValueNames { return [string[]]@($script:registry.Keys) }
          $o | Add-Member ScriptMethod GetValueKind { param($name) return [Microsoft.Win32.RegistryValueKind]::DWord }
          $o | Add-Member ScriptMethod GetValue { param($name) return $script:registry[$name] }
          $o | Add-Member ScriptMethod SetValue { param($name,$value,$kind) $script:registry[$name]=[int]$value }
          $o | Add-Member ScriptMethod DeleteValue { param($name,$throwOnMissing) $script:registry.Remove($name) }
          $o | Add-Member ScriptMethod Dispose { }
          return $o
        }
        function Read-RelayDword { param($name) if ($script:registry.ContainsKey($name)) { return [int]$script:registry[$name] }; return 0 }
        """;

    private const string MockWriter = """
        function Write-RelaySetting {
          param($name,$value)
          if ($script:service.Status -ne 'Stopped') { throw 'Settings changed while service running' }
          $script:writes++
          if ($script:failWrite -eq $name) { $script:failWrite=''; return }
          $script:settings[$name]=[string]$value
        }
        """;

    private const string MockRecovery = """
        function Save-RelayJournal($settings,$registry) {
          $script:savedSettings=@{}; foreach ($name in $settings.Keys) { $script:savedSettings[$name]=$settings[$name] }
          $script:savedRegistry=@{}; foreach ($name in $registry.Keys) { $script:savedRegistry[$name]=$registry[$name] }
        }
        function Clear-RelayJournal { $script:savedSettings=$null; $script:savedRegistry=$null }
        function Restore-RelayJournal {
          if ($null -eq $script:savedSettings) { return }
          if ($script:service.Status -ne 'Stopped') { $script:service.Stop() }
          foreach ($name in $script:savedSettings.Keys) { Write-RelaySetting $name $script:savedSettings[$name] }
          foreach ($name in $script:savedRegistry.Keys) {
            if ($script:savedRegistry[$name].exists) { $script:registry[$name]=$script:savedRegistry[$name].value }
            else { $script:registry.Remove($name) }
          }
          $script:service.Start()
          Clear-RelayJournal
        }
        """;

    [Theory]
    [InlineData("update")]
    [InlineData("disable")]
    [InlineData("unchanged")]
    [InlineData("readback-failure")]
    [InlineData("activation-failure")]
    public async Task Native_transaction_preserves_other_routes_and_credentials_and_restores_failures(string scenario)
    {
        var request = new SetMailRelayRequest
        { Enabled = scenario != "disable", Host = "relay.example.test", Port = 587, Username = "account", Password = scenario == "unchanged" ? "" : "new-password-Ş" };
        var setup = scenario switch
        {
            "readback-failure" => "$script:failWrite='smtpOutboundPassword'",
            "activation-failure" => "$script:failStart=$true",
            _ => ""
        };
        var script = MailEnableMailService.RelayMutationScript(request)
            .Replace(MailEnableMailService.PlatformHeader, MockPlatform + "\n" + setup)
            .Replace(MailEnableMailService.RelayRegistryHeader, MockRegistry)
            .Replace(MailEnableMailService.RelayWriteHeader, MockWriter)
            .Replace(MailEnableMailService.RelayRecoveryHeader, MockRecovery);
        var check = """
            if ($script:settings['tenantRoute'] -ne 'neighbor.example.test:2525' -or $script:settings['smtpSmartHostProcessingMode'] -ne '1') { throw 'Tenant routes changed' }
            if ($script:service.Status -ne 'Running') { throw 'SMTP not running' }
            """;
        check += scenario switch
        {
            "disable" => "\nif ($script:settings['smtpForwardAllOutboundEnabled'] -ne '0' -or $script:settings['smtpOutboundPassword'] -ne '' -or $script:settings['smtpOutboundUsername'] -ne '') { throw 'Disable left credentials or routing active' }",
            "unchanged" => "\nif ($script:writes -ne 0 -or $script:stops -ne 0) { throw 'Unchanged relay restarted SMTP' }",
            "readback-failure" or "activation-failure" => "\nforeach ($name in $script:before.Keys) { if ($script:settings[$name] -cne $script:before[$name]) { throw 'Rollback did not restore settings' } }",
            _ => "\nif ($script:settings['smtpOutboundPassword'] -cne 'new-password-Ş') { throw 'New password not saved' }"
        };
        // Run in a child scope so an idempotent script's return still lets assertions execute.
        var result = await ProcessRunner.RunPowerShellAsync("& {\n" + script + "\n}\n" + check, default);
        Assert.True(result.Success, result.StandardError);
        Assert.Equal(scenario is "readback-failure" or "activation-failure" ? "FAILED" : "OK", result.StandardOutput.Trim());
        Assert.DoesNotContain("password", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }
}
