using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailEnableCommandContractTests
{
    [Fact]
    public async Task Mailbox_mapping_cleanup_removes_all_target_routes_but_preserves_neighbor_mailbox()
    {
        const string fake = """
            $script:records = @{
              '[SMTP:info@example.test]' = '[SF:example.test/info]'
              '[SMTP:alias@example.test]' = '[SF:example.test/info]'
              '[SMTP:other@example.test]' = '[SF:example.test/other]'
            }
            function New-Object {
              param([string]$ComObject)
              $o = [pscustomobject]@{ Account=''; SourceAddress=''; DestinationAddress=''; Scope=''; Items=@(); Index=0 }
              $o | Add-Member ScriptMethod FindFirstAddressMap {
                $target=$this.DestinationAddress
                $this.Items=@($script:records.Keys | Where-Object { $script:records[$_] -eq $target })
                $this.Index=0
                if ($this.Items.Count -eq 0) { return 0 }
                $this.SourceAddress=$this.Items[0]; $this.Account='example.test'; return 1
              }
              $o | Add-Member ScriptMethod FindNextAddressMap {
                $this.Index++
                if ($this.Index -ge $this.Items.Count) { return 0 }
                $this.SourceAddress=$this.Items[$this.Index]; $this.Account='example.test'; return 1
              }
              $o | Add-Member ScriptMethod GetAddressMap {
                if (-not $script:records.ContainsKey($this.SourceAddress)) { return 0 }
                $this.DestinationAddress=$script:records[$this.SourceAddress]; $this.Account='example.test'; return 1
              }
              $o | Add-Member ScriptMethod RemoveAddressMap { $script:records.Remove($this.SourceAddress); return 1 }
              return $o
            }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(fake + "\n" + MailEnableMailService.RemoveMailboxMappingsScript("example.test", "info") + "\nif ($script:records.Count -ne 1 -or -not $script:records.ContainsKey('[SMTP:other@example.test]')) { throw 'Neighbor mailbox routing changed.' }", default);
        Assert.True(result.Success, result.StandardError);
        Assert.Contains("OK", result.StandardOutput);
    }

    [Fact]
    public async Task Autoresponder_unknown_state_or_unsupported_subject_never_claims_success()
    {
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        { KomutKosucusu = (_, _) => Task.FromResult("{\"enabled\":\"unknown\",\"message\":\"\"}") };
        await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() => service.GetAutoResponderAsync("info@example.test"));
        await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() => service.SetAutoResponderAsync(new() { Address = "info@example.test", Subject = "must not silently ignore" }));
    }

    [Fact]
    public async Task Real_32bit_command_preserves_non_ASCII_values_and_hides_error_source_lines()
    {
        const string value = "Şifre-ğİı-測試";
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance);
        var output = await service.RunComAsync("[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('" + value + "'))", default);
        Assert.Equal(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)), output.Trim());
        var error = await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() => service.RunComAsync("throw '" + value + "'", default));
        Assert.DoesNotContain(value, error.ToString());
        Assert.DoesNotContain(value, error.Details ?? "");
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("shared-domain", false)]
    [InlineData("mailbox", false)]
    [InlineData("map", false)]
    [InlineData("list", false)]
    [InlineData("group", false)]
    public async Task Domain_deletion_preserves_nonempty_and_shared_postoffices(string blocker, bool success)
    {
        var fake = $$"""
            $script:blocker = '{{blocker}}'
            $script:domainExists = $true
            $script:mutations = 0
            function New-Object {
              param([string]$ComObject)
              $o = [pscustomobject]@{ Name=''; Account=''; AccountName=''; Status=-1; DomainName=''; DomainRedirectionStatus=-1; DomainRedirectionHosts=''; Postoffice=''; Mailbox=''; Limit=-1; RedirectStatus=-1; RedirectAddress=''; SourceAddress=''; DestinationAddress=''; Scope=''; ListName=''; GroupName=''; RecipientAddress='' }
              switch ($ComObject) {
                'MEAOSM.Domain' {
                  $o | Add-Member ScriptMethod GetDomain { $this.AccountName='example.test'; if ($script:domainExists) { return 1 }; return 0 }
                  $o | Add-Member ScriptMethod FindFirstDomain { $this.DomainName=if ($script:blocker -eq 'shared-domain') { 'other.test' } else { 'example.test' }; return 1 }
                  $o | Add-Member ScriptMethod FindNextDomain { return 0 }
                  $o | Add-Member ScriptMethod RemoveDomain { $script:mutations++; $script:domainExists=$false; return 1 }
                }
                'MEAOPO.Mailbox' { $o | Add-Member ScriptMethod FindFirstMailbox { if ($script:blocker -eq 'mailbox') { return 1 }; return 0 } }
                'MEAOAM.AddressMap' { $o | Add-Member ScriptMethod FindFirstAddressMap { if ($script:blocker -eq 'map') { return 1 }; return 0 } }
                'MEAOLS.List' { $o | Add-Member ScriptMethod FindFirstList { if ($script:blocker -eq 'list') { return 1 }; return 0 } }
                'MEAOPO.Group' { $o | Add-Member ScriptMethod FindFirstGroup { if ($script:blocker -eq 'group') { return 1 }; return 0 } }
                'MEAOPO.PostOffice' {
                  $o | Add-Member ScriptMethod GetPostoffice { return 1 }
                  $o | Add-Member ScriptMethod RemovePostoffice { $script:mutations++; return 1 }
                }
                default { throw 'Unexpected COM class' }
              }
              return $o
            }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(fake + "\ntry {\n" + MailEnableMailService.DeleteEmptyDomainScript("example.test") + "\n} finally { Write-Output ('mutations=' + $script:mutations) }", default);
        Assert.Equal(success, result.Success);
        Assert.Contains(success ? "mutations=2" : "mutations=0", result.StandardOutput);
    }

    [Theory]
    [InlineData("mailbox")]
    [InlineData("map")]
    [InlineData("other-owner")]
    public async Task Existing_account_or_address_is_rejected_before_creation(string blocker)
    {
        var calls = 0;
        var fake = $$"""
            function New-Object {
              param([string]$ComObject)
              $o = [pscustomobject]@{ AccountName=''; DomainName=''; Postoffice=''; Mailbox=''; SourceAddress='' }
              $o | Add-Member ScriptMethod GetDomain { $this.AccountName='{{(blocker == "other-owner" ? "other.test" : "example.test")}}'; return 1 }
              $o | Add-Member ScriptMethod GetMailbox { return {{(blocker == "mailbox" ? 1 : 0)}} }
              $o | Add-Member ScriptMethod GetAddressMap { return {{(blocker == "map" ? 1 : 0)}} }
              return $o
            }
            """;
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = async (script, token) =>
            {
                calls++;
                var result = await ProcessRunner.RunPowerShellAsync(fake + "\n" + script, token);
                if (!result.Success) throw new AdaPanel.Core.Common.AppException("mail.command_failed", 500, 500);
                return result.StandardOutput;
            }
        };
        await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() => service.CreateAccountAsync(new() { Domain = "example.test", Address = "info", Password = "test-only-value" }));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("[SMTP:target@example.test]", "example.test", false, true)]
    [InlineData("[SF:example.test/info]", "example.test", false, false)]
    [InlineData("[LS:example.test/list]", "example.test", false, false)]
    [InlineData("[SMTP:target@example.test]", "other.test", false, false)]
    [InlineData("[SMTP:target@example.test]", "example.test", true, false)]
    public async Task Alias_delete_checks_record_type_owner_and_actual_removal(string destination, string owner, bool ignoresDelete, bool success)
    {
        var fake = $$"""
            $script:exists = $true
            function New-Object {
              param([string]$ComObject)
              $o = [pscustomobject]@{ SourceAddress=''; DestinationAddress='{{destination}}'; Account='{{owner}}' }
              $o | Add-Member ScriptMethod GetAddressMap { if ($script:exists) { return 1 }; return 0 }
              $o | Add-Member ScriptMethod RemoveAddressMap {
                if ('{{ignoresDelete}}' -eq 'False') { $script:exists = $false }; return 1
              }
              return $o
            }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(fake + "\n" + MailEnableMailService.DeleteAliasScript("info@example.test", "example.test"), default);
        Assert.Equal(success, result.Success);
        if (!success) Assert.DoesNotContain("OK", result.StandardOutput);
    }

    [Fact]
    public async Task Workspace_uses_documented_success_code_resets_filters_and_does_not_leak_neighbor_domain_aliases()
    {
        const string fake = """
            function New-Object {
              param([string]$ComObject)
              switch ($ComObject) {
                'MEAOPO.PostOffice' {
                  $o = [pscustomobject]@{ Name=''; Account=''; Status=0; Index=0 }
                  $o | Add-Member ScriptMethod FindFirstPostoffice { $this.Index=1; $this.Name='example.test'; $this.Status=1; return 1 }
                  $o | Add-Member ScriptMethod FindNextPostoffice {
                    if ($this.Name -ne '' -or $this.Status -ne -1) { throw 'Search filter was not reset.' }
                    if ($this.Index -eq 1) { $this.Index=2; $this.Name='another.test'; $this.Status=1; return 1 }; return 0
                  }
                  return $o
                }
                'MEAOPO.Mailbox' {
                  $o = [pscustomobject]@{ Postoffice=''; Mailbox=''; Status=0; Limit=0; RedirectStatus=0; RedirectAddress='' }
                  $o | Add-Member ScriptMethod FindFirstMailbox { $this.Mailbox='info'; $this.Status=1; $this.Limit=102400; return 1 }
                  $o | Add-Member ScriptMethod FindNextMailbox {
                    if ($this.Mailbox -ne '' -or $this.Status -ne -1) { throw 'Mailbox filter was not reset.' }; return 0
                  }
                  return $o
                }
                'MEAOAM.AddressMap' {
                  $o = [pscustomobject]@{ Account=''; SourceAddress=''; DestinationAddress=''; Scope=''; Index=0 }
                  $o | Add-Member ScriptMethod FindFirstAddressMap {
                    $this.Index=1; $this.SourceAddress='[SMTP:info@example.test.evil]'; $this.DestinationAddress='[SMTP:elsewhere@evil.test]'; return 1
                  }
                  $o | Add-Member ScriptMethod FindNextAddressMap {
                    if ($this.SourceAddress -ne '' -or $this.DestinationAddress -ne '') { throw 'Address filter was not reset.' }
                    if ($this.Index -eq 1) { $this.Index=2; $this.SourceAddress='[SMTP:sales@example.test]'; $this.DestinationAddress='[SMTP:info@example.test]'; return 1 }; return 0
                  }
                  return $o
                }
                default { throw 'Unexpected COM operation.' }
              }
            }
            """;
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = async (script, token) =>
            {
                if (script.Contains("function Read-ListMembers", StringComparison.Ordinal)) return "{\"lists\":[]}";
                var result = await ProcessRunner.RunPowerShellAsync(fake + "\n" + script, token);
                Assert.True(result.Success, result.StandardError);
                return result.StandardOutput;
            }
        };
        var result = await service.GetWorkspaceAsync("example.test");
        Assert.True(result.Configured);
        Assert.True(result.AccountsMeasured);
        Assert.Equal(2, result.Domains.Count);
        Assert.All(result.Domains, domain => { Assert.True(domain.AccountCountMeasured); Assert.Equal(1, domain.AccountCount); });
        Assert.Equal(100, Assert.Single(result.Accounts).MaxSizeMB);
        Assert.Equal("sales@example.test", Assert.Single(result.Aliases).Address);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Mailbox_creation_routes_SMTP_and_uses_real_quota_edit_signature(bool conflictingAddress, bool success)
    {
        var fake = $$"""
            $script:exists = {{(conflictingAddress ? "$true" : "$false")}}
            $script:destination = '{{(conflictingAddress ? "[SF:other/owner]" : "")}}'
            function New-Object {
              param([string]$ComObject)
              switch ($ComObject) {
                'MEAOPO.Mailbox' {
                  $o = [pscustomobject]@{ Postoffice=''; Mailbox=''; Status=1; Limit=-1; RedirectStatus=2; RedirectAddress='[SMTP:copy@example.test]'; Size=9 }
                  $o | Add-Member ScriptMethod GetMailbox { return 1 }
                  $o | Add-Member ScriptMethod EditMailbox {
                    param($po,$name,$redirect,$redirectStatus,$status,$limit,$size)
                    if ($po -ne 'example.test' -or $name -ne 'info' -or $limit -ne 102400 -or $redirectStatus -ne 2 -or $size -ne 9) { throw 'Incorrect mailbox update signature or lost settings.' }
                    return 1
                  }; return $o
                }
                'MEAOAM.AddressMap' {
                  $o = [pscustomobject]@{ Account=''; SourceAddress=''; DestinationAddress=''; Scope='' }
                  $o | Add-Member ScriptMethod GetAddressMap {
                    if ($script:exists) { $this.DestinationAddress=$script:destination; return 1 }; return 0
                  }
                  $o | Add-Member ScriptMethod AddAddressMap {
                    if ($this.SourceAddress -ne '[SMTP:info@example.test]' -or $this.DestinationAddress -ne '[SF:example.test/info]') { throw 'Wrong delivery mapping.' }
                    $script:destination=$this.DestinationAddress; $script:exists=$true; return 1
                  }; return $o
                }
                default { throw 'Unexpected COM operation.' }
              }
            }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(fake + "\n" + MailEnableMailService.MailboxMappingScript("example.test", "info", "info@example.test", 100), default);
        Assert.Equal(success, result.Success);
        if (success) Assert.Contains("OK", result.StandardOutput);
        else Assert.DoesNotContain("OK", result.StandardOutput);
    }
}
