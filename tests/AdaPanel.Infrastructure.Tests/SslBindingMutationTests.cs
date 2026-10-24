using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslBindingMutationTests
{
    private const string NewHash = "cccccccccccccccccccccccccccccccccccccccc";

    [Theory]
    [InlineData("normal")]
    [InlineData("byte-hash")]
    [InlineData("ipv6")]
    [InlineData("flags-125")]
    [InlineData("webhosting")]
    [InlineData("store-normalized-uppercase")]
    [InlineData("case-normalization")]
    public async Task Existing_binding_updates_in_place_preserving_IP_flags_and_neighbors(string scenario)
    {
        var result = await Run(scenario);
        Assert.Equal("OK", result.GetProperty("result").GetString());
        Assert.Equal("", result.GetProperty("error").GetString());
        Assert.Equal(1, result.GetProperty("attaches").GetInt32());
        Assert.Equal(0, result.GetProperty("created").GetInt32());
        Assert.Equal(0, result.GetProperty("removed").GetInt32());
        Assert.Equal(NewHash, result.GetProperty("hash").GetString());
        Assert.Equal(scenario == "webhosting" ? "WebHosting" : "My", result.GetProperty("store").GetString(), ignoreCase: true);
    }

    [Theory]
    [InlineData("noop")]
    [InlineData("noop-webhosting-with-my-copy")]
    public async Task Same_certificate_in_current_private_key_store_is_a_true_noop(string scenario)
    {
        var result = await Run(scenario);
        Assert.Equal("OK", result.GetProperty("result").GetString());
        Assert.Equal(0, result.GetProperty("attaches").GetInt32());
        Assert.Equal(0, result.GetProperty("created").GetInt32());
        Assert.Equal(0, result.GetProperty("removed").GetInt32());
        if (scenario.Contains("webhosting")) Assert.Equal("WebHosting", result.GetProperty("store").GetString());
    }

    [Theory]
    [InlineData("ccs")]
    [InlineData("ambiguous")]
    [InlineData("shared-sni-other-ip")]
    [InlineData("shared-cross-flags")]
    [InlineData("shared-cross-flags-reverse")]
    [InlineData("shared-nonsni-hosted")]
    [InlineData("shared-nonsni-wildcard")]
    [InlineData("invalid-empty-sni")]
    [InlineData("inventory-unreadable")]
    [InlineData("hash-unreadable")]
    [InlineData("store-unreadable")]
    [InlineData("flags-unreadable")]
    [InlineData("new-certificate-missing")]
    [InlineData("current-user-only")]
    [InlineData("new-private-key-missing")]
    [InlineData("old-private-key-missing")]
    [InlineData("certificate-less")]
    public async Task Unmeasured_ambiguous_shared_or_unrestorable_targets_are_rejected_before_any_write(string scenario)
    {
        var result = await Run(scenario);
        Assert.NotEmpty(result.GetProperty("error").GetString()!);
        Assert.Equal(0, result.GetProperty("attaches").GetInt32());
        Assert.Equal(0, result.GetProperty("created").GetInt32());
        Assert.Equal(0, result.GetProperty("removed").GetInt32());
    }

    [Theory]
    [InlineData("new-named")]
    [InlineData("new-empty")]
    [InlineData("new-default-store")]
    public async Task Only_absent_exact_host_is_created_with_appropriate_SNI_and_other_hosts_preserved(string scenario)
    {
        var result = await Run(scenario);
        Assert.Equal("OK", result.GetProperty("result").GetString());
        Assert.Equal("", result.GetProperty("error").GetString());
        Assert.Equal(1, result.GetProperty("created").GetInt32());
        Assert.Equal(1, result.GetProperty("attaches").GetInt32());
        Assert.Equal(0, result.GetProperty("removed").GetInt32());
        Assert.Equal(scenario == "new-empty" ? 0 : 1, result.GetProperty("flags").GetInt32());
        Assert.Equal(NewHash, result.GetProperty("hash").GetString());
    }

    [Theory]
    [InlineData("attach-failure")]
    [InlineData("readback-failure")]
    public async Task Failed_existing_update_restores_previous_certificate_without_recreating_binding(string scenario)
    {
        var result = await Run(scenario);
        Assert.Equal("SSL_BINDING_FAILED_ROLLED_BACK", result.GetProperty("error").GetString());
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", result.GetProperty("hash").GetString());
        Assert.Equal(2, result.GetProperty("attaches").GetInt32());
        Assert.Equal(0, result.GetProperty("created").GetInt32());
        Assert.Equal(0, result.GetProperty("removed").GetInt32());
    }

    [Theory]
    [InlineData("new-attach-failure")]
    [InlineData("new-default-store-failure")]
    public async Task Failed_new_attachment_removes_only_the_owned_new_binding(string scenario)
    {
        var result = await Run(scenario);
        Assert.Equal("SSL_BINDING_FAILED_ROLLED_BACK", result.GetProperty("error").GetString());
        Assert.Equal(1, result.GetProperty("created").GetInt32());
        Assert.Equal(1, result.GetProperty("removed").GetInt32());
        Assert.False(result.GetProperty("targetExists").GetBoolean());
    }

    [Theory]
    [InlineData("external-certificate")]
    [InlineData("external-flags")]
    [InlineData("new-external-certificate")]
    [InlineData("new-create-failure")]
    [InlineData("rollback-failure")]
    public async Task Externally_changed_or_unverifiable_rollback_is_explicit_and_never_overwritten(string scenario)
    {
        var result = await Run(scenario);
        Assert.Equal("SSL_BINDING_ROLLBACK_UNVERIFIED", result.GetProperty("error").GetString());
        Assert.Equal(0, result.GetProperty("removed").GetInt32());
        if (scenario.Contains("external-certificate")) Assert.Equal("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", result.GetProperty("hash").GetString());
        if (scenario == "external-flags") Assert.Equal(65, result.GetProperty("flags").GetInt32());
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(0, "FAILED")]
    [InlineData(0, "OK\nwarning")]
    [InlineData(1, "OK")]
    public async Task Command_exit_success_without_exact_verified_OK_is_not_reported_as_success(int exitCode, string output)
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=example.test", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var service = new WinAcmeSslService(null!, null!, null!, null!, null!, NullLogger<WinAcmeSslService>.Instance)
        { HttpsBindingCommand = (_, _) => Task.FromResult(new CommandResult { ExitCode=exitCode, StandardOutput=output }) };
        var error = await Assert.ThrowsAsync<AppException>(() => service.EnsureHttpsBindingAsync("target", "example.test", 443, certificate, default));
        Assert.Equal("ssl.binding_failed", error.MessageKey);
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("env-path", true)]
    [InlineData("changed-id", false)]
    [InlineData("changed-pool", false)]
    [InlineData("changed-path", false)]
    [InlineData("changed-hash", false)]
    [InlineData("changed-flags", false)]
    public async Task Approved_existing_binding_snapshot_is_checked_in_the_native_script(string scenario, bool allowed)
    {
        var expected = new SslBindingMutation.ExpectedBinding(scenario == "changed-id" ? 99 : 7,
            scenario == "changed-path" ? "C:\\foreign" : "%SystemDrive%\\site",
            scenario == "changed-pool" ? "foreign" : "pool", "192.0.2.10:443:example.test",
            scenario == "changed-hash" ? NewHash : "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "My",
            scenario == "changed-flags" ? 1 : 5, "WebHosting");
        var result = await Run(scenario, expected);
        Assert.Equal(allowed ? 1 : 0, result.GetProperty("attaches").GetInt32());
        Assert.Equal(0, result.GetProperty("created").GetInt32());
        if (allowed) { Assert.Equal("OK", result.GetProperty("result").GetString()); Assert.Equal("WebHosting", result.GetProperty("store").GetString()); }
        else Assert.NotEmpty(result.GetProperty("error").GetString()!);
    }

    private static async Task<JsonElement> Run(string scenario, SslBindingMutation.ExpectedBinding? expected = null)
    {
        var prefix = "$script:scenario='" + scenario + "'\n" + MockEnvironment;
        var host = scenario is "new-empty" or "invalid-empty-sni" ? "" : "example.test";
        var script = prefix + "\n$script:result=''; $script:failure=''\ntry { $script:result=(@(& {\n"
            + SslBindingMutation.Script("target", host, 443, NewHash, expected)
            + "\n}) -join [Environment]::NewLine) } catch { $script:failure=$_.Exception.Message }\n" + Checks;
        var result = await ProcessRunner.RunPowerShellAsync(script, default);
        Assert.True(result.Success, result.StandardError + "\n" + result.StandardOutput);
        return JsonDocument.Parse(result.StandardOutput).RootElement.Clone();
    }

    [Theory]
    [InlineData("new-named", true)]
    [InlineData("normal", false)]
    public async Task An_approved_absent_host_cannot_adopt_a_foreign_binding(string scenario, bool allowed)
    {
        var expected = new SslBindingMutation.ExpectedBinding(7, "%SystemDrive%\\site", "pool",
            "*:443:example.test", "", "", 1, "My", MustBeAbsent: true);
        var result = await Run(scenario, expected);
        Assert.Equal(allowed ? 1 : 0, result.GetProperty("attaches").GetInt32());
        Assert.Equal(allowed ? 1 : 0, result.GetProperty("created").GetInt32());
        if (!allowed) Assert.Contains("Approved absent binding changed", result.GetProperty("error").GetString());
    }

    private const string MockEnvironment = """
        $ErrorActionPreference='Stop'
        $script:attaches=0; $script:creates=0; $script:removes=0; $script:failedRead=$false
        $script:old='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; $script:new='cccccccccccccccccccccccccccccccccccccccc'
        $script:other='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
        $script:certificates=@{}
        foreach ($storeName in @('My','WebHosting')) {
          foreach ($hash in @($script:old,$script:new)) { $script:certificates['Cert:\LocalMachine\'+$storeName+'\'+$hash]=[pscustomobject]@{Thumbprint=$hash;HasPrivateKey=$true} }
        }
        function Import-Module { param($Name,$ErrorAction) if ($Name -ne 'WebAdministration') { throw 'Unexpected module' } }
        function Test-Path { param($LiteralPath,$ErrorAction) return $script:certificates.ContainsKey($LiteralPath) }
        function Get-Item { param($LiteralPath,$ErrorAction) if (-not $script:certificates.ContainsKey($LiteralPath)) { throw 'Missing fixture certificate' }; return $script:certificates[$LiteralPath] }
        function Make-Binding($info,$flags,$hash,$storeName) {
          $b=[pscustomobject]@{protocol='https';bindingInformation=$info;sslFlags=$flags;certificateHash=$hash;certificateStoreName=$storeName}
          $b | Add-Member ScriptMethod AddSslCertificate {
            param($hash,$storeName)
            $script:attaches++
            $this.certificateHash=$hash; $this.certificateStoreName=$storeName
            if ($script:scenario -eq 'store-normalized-uppercase') { $this.certificateStoreName=$storeName.ToUpperInvariant() }
            if ($script:scenario -eq 'case-normalization') { $this.bindingInformation=$this.bindingInformation.ToUpperInvariant() }
            if ($script:attaches -eq 1 -and $script:scenario -in @('external-certificate','new-external-certificate')) { $this.certificateHash='eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'; throw 'External certificate arrived' }
            if ($script:attaches -eq 1 -and $script:scenario -eq 'external-flags') { $this.sslFlags=65; throw 'External flags arrived' }
            if ($script:scenario -eq 'rollback-failure') { throw 'Attachment failed' }
            if ($script:attaches -eq 1 -and $script:scenario -in @('attach-failure','new-attach-failure','new-default-store-failure')) { throw 'Partial attachment failed' }
          }
          return $b
        }
        $script:target=Make-Binding '192.0.2.10:443:example.test' 5 $script:old 'My'
        $script:neighbor=Make-Binding '192.0.2.20:443:neighbor.test' 1 $script:other 'WebHosting'
        $script:bindings=@($script:target); $script:neighbors=@($script:neighbor)
        switch ($script:scenario) {
          'byte-hash' { $script:target.certificateHash=[byte[]]@(1..20 | ForEach-Object { 170 }) }
          'ipv6' { $script:target.bindingInformation='[2001:db8::1]:443:example.test' }
          'flags-125' { $script:target.sslFlags=125 }
          'webhosting' { $script:certificates.Remove('Cert:\LocalMachine\My\'+$script:new) }
          'noop' { $script:target.certificateHash=$script:new }
          'noop-webhosting-with-my-copy' { $script:target.certificateHash=$script:new; $script:target.certificateStoreName='WebHosting' }
          'ccs' { $script:target.sslFlags=3 }
          'ambiguous' { $script:bindings+=Make-Binding '192.0.2.11:443:example.test' 1 $script:old 'My' }
          'shared-sni-other-ip' { $script:neighbor.bindingInformation='192.0.2.99:443:EXAMPLE.TEST' }
          'shared-cross-flags' { $script:neighbor.bindingInformation='192.0.2.99:443:EXAMPLE.TEST'; $script:neighbor.sslFlags=0 }
          'shared-cross-flags-reverse' { $script:neighbor.bindingInformation='192.0.2.99:443:EXAMPLE.TEST'; $script:target.sslFlags=0 }
          'shared-nonsni-hosted' { $script:target.sslFlags=4; $script:neighbor.sslFlags=0; $script:neighbor.bindingInformation='192.0.2.10:443:other.test' }
          'shared-nonsni-wildcard' { $script:target.sslFlags=0; $script:target.bindingInformation='*:443:example.test'; $script:neighbor.sslFlags=0; $script:neighbor.bindingInformation='0.0.0.0:443:other.test' }
          'invalid-empty-sni' { $script:target.bindingInformation='*:443:'; $script:target.sslFlags=1 }
          'hash-unreadable' { $script:target.certificateHash=[pscustomobject]@{unknown=1} }
          'store-unreadable' { $script:target.PSObject.Properties.Remove('certificateStoreName'); $script:target | Add-Member ScriptProperty certificateStoreName { throw 'Store read denied' } }
          'flags-unreadable' { $script:target.PSObject.Properties.Remove('sslFlags'); $script:target | Add-Member ScriptProperty sslFlags { throw 'Flags read denied' } }
          'old-private-key-missing' { $script:certificates['Cert:\LocalMachine\My\'+$script:old].HasPrivateKey=$false }
          'certificate-less' { $script:target.certificateHash=''; $script:target.certificateStoreName='' }
        }
        if ($script:scenario -in @('new-certificate-missing','current-user-only','new-private-key-missing')) {
          foreach ($storeName in @('My','WebHosting')) {
            if ($script:scenario -eq 'new-private-key-missing') { $script:certificates['Cert:\LocalMachine\'+$storeName+'\'+$script:new].HasPrivateKey=$false }
            else { $script:certificates.Remove('Cert:\LocalMachine\'+$storeName+'\'+$script:new) }
          }
          if ($script:scenario -eq 'current-user-only') { $script:certificates['Cert:\CurrentUser\My\'+$script:new]=[pscustomobject]@{Thumbprint=$script:new;HasPrivateKey=$true} }
        }
        if ($script:scenario -like 'new-*' -and $script:scenario -notin @('new-certificate-missing','new-private-key-missing')) {
          if ($script:scenario -eq 'new-empty') { $script:target.bindingInformation='*:443:example.test'; $script:target.sslFlags=1 }
          else { $script:target.bindingInformation='*:443:'; $script:target.sslFlags=0 }
        }
        $script:initialInfo=$script:target.bindingInformation
        $script:initialFlags=if ($script:scenario -eq 'flags-unreadable') { 0 } else { $script:target.sslFlags }
        function Get-Website {
          param($ErrorAction)
          if ($script:scenario -eq 'inventory-unreadable') { throw 'IIS inventory denied' }
          if ($script:scenario -eq 'readback-failure' -and $script:attaches -eq 1 -and -not $script:failedRead) { $script:failedRead=$true; throw 'Readback denied' }
          $siteName=if ($script:scenario -eq 'case-normalization' -and $script:attaches -gt 0) { 'TARGET' } else { 'target' }
          $physical=if($script:scenario -eq 'env-path') {'%SystemDrive%\site'} else {[Environment]::ExpandEnvironmentVariables('%SystemDrive%\site')}
          [pscustomobject]@{Name=$siteName;Id=7;physicalPath=$physical;applicationPool='pool';Bindings=[pscustomobject]@{Collection=@($script:bindings)}}
          [pscustomobject]@{Name='neighbor';Bindings=[pscustomobject]@{Collection=@($script:neighbors)}}
        }
        function Get-WebBinding { param($Name,$Protocol,$ErrorAction) if ($Name -ne 'target' -or $Protocol -ne 'https') { throw 'Unexpected binding lookup' }; return $script:bindings }
        function New-WebBinding {
          param($Name,$Protocol,$IPAddress,$Port,$HostHeader,$SslFlags,$ErrorAction)
          if ($Name -ne 'target' -or $IPAddress -ne '*' -or $Protocol -ne 'https') { throw 'Unexpected binding creation' }
          $script:creates++
          $storeName=if($script:scenario -like 'new-default-store*') {'My'} else {''}
          $script:bindings+=Make-Binding ($IPAddress+':'+$Port+':'+$HostHeader) $SslFlags '' $storeName
          if ($script:scenario -eq 'new-create-failure') { throw 'Creation result is unverified' }
        }
        function Remove-WebBinding {
          param([Parameter(ValueFromPipeline=$true)]$InputObject)
          process {
            if ($InputObject -eq $script:target) { throw 'Existing binding was removed' }
            $script:removes++
            $script:bindings=@($script:bindings | Where-Object { -not [object]::ReferenceEquals($_,$InputObject) })
          }
        }
        """;

    private const string Checks = """
        if ($script:neighbor.certificateHash -cne $script:other -or $script:neighbor.certificateStoreName -cne 'WebHosting') { throw 'Neighbor certificate changed' }
        if ($script:target.bindingInformation -ine $script:initialInfo) { throw 'Existing binding IP or host changed' }
        if ($script:scenario -notin @('external-flags','flags-unreadable') -and $script:target.sslFlags -ne $script:initialFlags) { throw 'Existing SSL flags changed' }
        $hostName=if($script:scenario -in @('new-empty','invalid-empty-sni')) {''} else {'example.test'}
        $selected=@($script:bindings | Where-Object { $_.bindingInformation -match (':443:'+[regex]::Escape($hostName)+'$') })
        $hash=''; $storeName=''; $flags=0
        if ($selected.Count -gt 0) {
          $hash=$selected[0].certificateHash
          if ($script:scenario -eq 'hash-unreadable') { $hash='' }
          if ($script:scenario -ne 'store-unreadable') { $storeName=$selected[0].certificateStoreName }
          if ($script:scenario -ne 'flags-unreadable') { $flags=[int]$selected[0].sslFlags }
        }
        [pscustomobject]@{result=$script:result;error=$script:failure;attaches=$script:attaches;created=$script:creates;removed=$script:removes;hash=$hash;store=$storeName;flags=$flags;targetExists=($selected.Count -gt 0)} | ConvertTo-Json -Compress
        """;
}
