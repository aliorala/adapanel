using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailEnableListContractTests
{
    [Theory]
    [InlineData("subscribers")]
    [InlineData("allowed")]
    [InlineData("native-members")]
    [InlineData("unknown")]
    public async Task Authenticated_or_unknown_policies_are_rejected_before_any_server_mutation(string policy)
    {
        var calls = 0;
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        { KomutKosucusu = (_, _) => { calls++; return Task.FromResult("OK"); } };
        var error = await Assert.ThrowsAsync<AppException>(() => service.CreateMailListAsync(new()
        { Domain = "example.test", Address = "news", PostingPolicy = policy }));
        Assert.Equal("mail.mailenable_list_policy_unsupported", error.Message);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"lists\":null}", true)]
    [InlineData("{\"lists\":[{\"address\":\"news@example.test\"}]}", true)]
    [InlineData("{\"lists\":[]}", false)]
    public async Task Missing_list_inventory_is_not_reported_as_an_empty_list(string output, bool fails)
    {
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        { KomutKosucusu = (_, _) => Task.FromResult(output) };
        if (fails) await Assert.ThrowsAsync<AppException>(() => service.ReadNativeListsAsync("example.test", default));
        else Assert.Empty(await service.ReadNativeListsAsync("example.test", default));
    }

    [Fact]
    public async Task Native_members_policy_is_not_relabelled_as_authenticated_subscribers()
    {
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = (_, _) => Task.FromResult("""
                {"lists":[{"address":"news@example.test","description":"Native list","postingPolicy":"native-members","recipients":["one@outside.test"],"aliases":["alias@example.test"]}]}
                """)
        };
        var item = Assert.Single(await service.ReadNativeListsAsync("example.test", default));
        Assert.Equal("native-members", item.PostingPolicy);
        Assert.Equal("one@outside.test", Assert.Single(item.Recipients));
    }

    [Fact]
    public async Task Create_update_alias_inventory_delete_preserves_neighbor_and_native_fields()
    {
        var script = MailEnableMailService.CreateNativeListScript("example.test", "news", "Duyuru 'Deneme' Şİğ", ["one@outside.test"]) + "\n" +
            MailEnableMailService.UpdateNativeListScript("example.test", "news@example.test", null, ["two@outside.test"], ["one@outside.test"], false) + "\n" +
            MailEnableMailService.ListAliasScript("example.test", "news@example.test", "example.test", "alias@example.test", true) + "\n" +
            MailEnableMailService.ReadNativeListsScript("example.test") + "\n" +
            MailEnableMailService.ListAliasScript("example.test", "news@example.test", "example.test", "alias@example.test", false) + "\n" +
            MailEnableMailService.DeleteNativeListScript("example.test", "news@example.test") + "\n" + """
                if ($script:fakeLists.Count -ne 0 -or $script:fakeRoutes.Count -ne 1 -or -not $script:fakeRoutes.ContainsKey('[SMTP:neighbor@example.test]')) { throw 'Neighbor route changed or list leaked.' }
                """;
        var result = await ProcessRunner.RunPowerShellAsync(FakeCom + "\n" + script, default);
        Assert.True(result.Success, result.StandardError);
        Assert.Contains("two@outside.test", result.StandardOutput);
        Assert.Contains("alias@example.test", result.StandardOutput);
        Assert.Contains("Duyuru", result.StandardOutput);
    }

    [Fact]
    public async Task Membership_failure_restores_old_members_without_changing_policy_or_other_native_fields()
    {
        var script = MailEnableMailService.CreateNativeListScript("example.test", "news", "Native list", ["one@outside.test"]) + "\n" + """
            $script:fakeLists['example.test/news'].PostingMode=2
            $script:fakeLists['example.test/news'].ListType=1
            $script:fakeLists['example.test/news'].Password='retained-secret'
            $script:fakeLists['example.test/news'].HeaderAnnotation='custom-header'
            $script:failRemoveMemberOnce=$true
            """ + "\ntry {\n" +
            MailEnableMailService.UpdateNativeListScript("example.test", "news@example.test", ["two@outside.test"], [], [], false) +
            "\nthrow 'Expected update failure'\n} catch { if ($_.Exception.Message -eq 'Expected update failure') { throw } }\n" + """
            $l=$script:fakeLists['example.test/news']
            if ($l.PostingMode -ne 2 -or $l.ListType -ne 1 -or $l.Password -cne 'retained-secret' -or $l.HeaderAnnotation -cne 'custom-header') { throw 'Native settings changed.' }
            $members=$script:fakeMembers['example.test/news']
            if ($members.Count -ne 1 -or -not $members.ContainsKey('[SMTP:one@outside.test]')) { throw 'Members were not restored.' }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(FakeCom + "\n" + script, default);
        Assert.True(result.Success, result.StandardError);
        Assert.DoesNotContain("retained-secret", result.StandardOutput);
    }

    [Fact]
    public async Task Explicit_anyone_policy_preserves_other_list_settings_and_uses_documented_EditList_arguments()
    {
        var script = MailEnableMailService.CreateNativeListScript("example.test", "news", "Native list", ["one@outside.test"]) + "\n" + """
            $script:fakeLists['example.test/news'].PostingMode=2
            $script:fakeLists['example.test/news'].HeaderAnnotation='keep-header'
            $script:fakeLists['example.test/news'].SubscriptionMode=1
            """ + "\n" +
            MailEnableMailService.UpdateNativeListScript("example.test", "news@example.test", null, [], [], true) + "\n" + """
            $l=$script:fakeLists['example.test/news']
            if ($l.PostingMode -ne 1 -or $l.HeaderAnnotation -cne 'keep-header' -or $l.SubscriptionMode -ne 1) { throw 'Unrelated list settings changed.' }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(FakeCom + "\n" + script, default);
        Assert.True(result.Success, result.StandardError);
    }

    [Fact]
    public async Task Failed_delete_restores_complete_list_members_and_routes()
    {
        var script = MailEnableMailService.CreateNativeListScript("example.test", "news", "Before delete", ["one@outside.test"]) + "\n" +
            MailEnableMailService.ListAliasScript("example.test", "news@example.test", "example.test", "alias@example.test", true) + "\n" + """
            $script:fakeLists['example.test/news'].PostingMode=2
            $script:fakeLists['example.test/news'].Password='not-for-logs'
            $script:failRemoveListOnce=$true
            """ + "\ntry {\n" +
            MailEnableMailService.DeleteNativeListScript("example.test", "news@example.test") +
            "\nthrow 'Expected deletion failure'\n} catch { if ($_.Exception.Message -eq 'Expected deletion failure') { throw } }\n" + """
            if ($script:fakeLists['example.test/news'].Password -cne 'not-for-logs' -or $script:fakeLists['example.test/news'].PostingMode -ne 2) { throw 'Native list not restored.' }
            if ($script:fakeMembers['example.test/news'].Count -ne 1 -or $script:fakeRoutes.Count -ne 3) { throw 'Members or routes not restored.' }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(FakeCom + "\n" + script, default);
        Assert.True(result.Success, result.StandardError);
        Assert.DoesNotContain("not-for-logs", result.StandardOutput);
    }

    [Theory]
    [InlineData("neighbor@example.test")]
    [InlineData("news@example.test")]
    public async Task Alias_change_cannot_overwrite_mailbox_or_remove_primary_address(string alias)
    {
        var script = MailEnableMailService.CreateNativeListScript("example.test", "news", "List", []) +
            "\ntry {\n" + MailEnableMailService.ListAliasScript("example.test", "news@example.test", "example.test", alias, false) +
            "\nthrow 'Expected ownership failure'\n} catch { if ($_.Exception.Message -eq 'Expected ownership failure') { throw } }\n" + """
            if ($script:fakeRoutes.Count -ne 2 -or $script:fakeRoutes['[SMTP:neighbor@example.test]'].DestinationAddress -cne '[SF:example.test/neighbor]') { throw 'Unrelated route changed.' }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(FakeCom + "\n" + script, default);
        Assert.True(result.Success, result.StandardError);
    }

    [Fact]
    public async Task Self_referential_alias_is_rejected_without_mutations()
    {
        var script = MailEnableMailService.CreateNativeListScript("example.test", "news", "List", ["alias@example.test"]) +
            "\ntry {\n" + MailEnableMailService.ListAliasScript("example.test", "news@example.test", "example.test", "alias@example.test", true) +
            "\nthrow 'Expected cycle rejection'\n} catch { if ($_.Exception.Message -eq 'Expected cycle rejection') { throw } }\n" + """
            if ($script:fakeRoutes.Count -ne 2) { throw 'Cyclic alias was installed.' }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(FakeCom + "\n" + script, default);
        Assert.True(result.Success, result.StandardError);
    }

    // Stateful COM contract fake, executed by real Windows PowerShell. It checks search
    // reset semantics and exercises readbacks/rollback without installing a mail server.
    private const string FakeCom = """
        $script:fakeLists=@{}; $script:fakeMembers=@{}; $script:fakeRoutes=@{
          '[SMTP:neighbor@example.test]'=@{ Account='example.test'; SourceAddress='[SMTP:neighbor@example.test]'; DestinationAddress='[SF:example.test/neighbor]'; Scope='0' }
        }
        $script:listFields=@('AccountName','ListName','ListAddress','Description','ListType','ListStatus','HeaderAnnotationStatus','HeaderAnnotation','FooterAnnotationStatus','FooterAnnotation','ModeratorAddress','PostingMode','SubscriptionMode','Password')
        $script:failRemoveMemberOnce=$false; $script:failRemoveListOnce=$false
        function New-Object {
          param([string]$ComObject)
          $o=[pscustomobject]@{
            AccountName=''; ListName=''; ListAddress=''; Description=''; ListType=-1; ListStatus=-1
            HeaderAnnotationStatus=0; HeaderAnnotation=''; FooterAnnotationStatus=0; FooterAnnotation=''
            ModeratorAddress=''; PostingMode=1; SubscriptionMode=1; Password=''
            Account=''; SourceAddress=''; DestinationAddress=''; Scope=''; DomainName=''
            Address=''; ListMemberType=-1; Status=-1; Items=@(); Index=0; Key=''
          }
          switch ($ComObject) {
            'MEAOSM.Domain' {
              $o | Add-Member ScriptMethod GetDomain { $this.AccountName='example.test'; return 1 }
            }
            'MEAOLS.List' {
              $o | Add-Member ScriptMethod GetList {
                $key="$($this.AccountName)/$($this.ListName)"
                if (-not $script:fakeLists.ContainsKey($key)) { return 0 }
                foreach ($field in $script:listFields) { $this.$field=$script:fakeLists[$key][$field] }
                return 1
              }
              $o | Add-Member ScriptMethod AddList {
                $key="$($this.AccountName)/$($this.ListName)"
                if ($script:fakeLists.ContainsKey($key)) { return 0 }
                $copy=@{}; foreach ($field in $script:listFields) { $copy[$field]=$this.$field }
                $script:fakeLists[$key]=$copy; $script:fakeMembers[$key]=@{}; return 1
              }
              $o | Add-Member ScriptMethod RemoveList {
                $key="$($this.AccountName)/$($this.ListName)"
                $script:fakeLists.Remove($key); $script:fakeMembers.Remove($key)
                if ($script:failRemoveListOnce) { $script:failRemoveListOnce=$false; return 0 }; return 1
              }
              $o | Add-Member ScriptMethod FindFirstList {
                if ($this.ListName -ne '' -or $this.ListStatus -ne -1 -or $this.ListType -ne -1) { throw 'List filters not reset.' }
                $po=$this.AccountName; $this.Items=@($script:fakeLists.Keys | Where-Object { $_.StartsWith("$po/") } | Sort-Object); $this.Index=0
                if ($this.Items.Count -eq 0) { return 0 }
                foreach ($field in $script:listFields) { $this.$field=$script:fakeLists[$this.Items[0]][$field] }; return 1
              }
              $o | Add-Member ScriptMethod FindNextList {
                if ($this.ListName -ne '' -or $this.ListStatus -ne -1 -or $this.ListType -ne -1) { throw 'List filters not reset.' }
                $this.Index++; if ($this.Index -ge $this.Items.Count) { return 0 }
                foreach ($field in $script:listFields) { $this.$field=$script:fakeLists[$this.Items[$this.Index]][$field] }; return 1
              }
              $o | Add-Member ScriptMethod EditList {
                if ($args.Count -ne 27 -or $args[11] -ne -1 -or $args[12] -cne '(Nil)' -or $args[25] -ne -1) { throw 'Incorrect optional-argument preservation.' }
                $key="$($this.AccountName)/$($this.ListName)"; $script:fakeLists[$key].PostingMode=$args[26]; return 1
              }
            }
            'MEAOLS.ListMember' {
              $o | Add-Member ScriptMethod FindFirstListMember {
                if ($this.Address -ne '' -or $this.Status -ne -1 -or $this.ListMemberType -ne -1) { throw 'Member filters not reset.' }
                $this.Key="$($this.AccountName)/$($this.ListName)"; $this.Items=@($script:fakeMembers[$this.Key].Keys | Sort-Object); $this.Index=0
                if ($this.Items.Count -eq 0) { return 0 }
                $this.Address=$this.Items[0]; $entry=$script:fakeMembers[$this.Key][$this.Address]; $this.Status=$entry.Status; $this.ListMemberType=$entry.ListMemberType; return 1
              }
              $o | Add-Member ScriptMethod FindNextListMember {
                if ($this.Address -ne '' -or $this.Status -ne -1 -or $this.ListMemberType -ne -1) { throw 'Member filters not reset.' }
                $this.Index++; if ($this.Index -ge $this.Items.Count) { return 0 }
                $this.Address=$this.Items[$this.Index]; $entry=$script:fakeMembers[$this.Key][$this.Address]; $this.Status=$entry.Status; $this.ListMemberType=$entry.ListMemberType; return 1
              }
              $o | Add-Member ScriptMethod AddListMember {
                $key="$($this.AccountName)/$($this.ListName)"
                $script:fakeMembers[$key][$this.Address]=@{ Status=$this.Status; ListMemberType=$this.ListMemberType }; return 1
              }
              $o | Add-Member ScriptMethod RemoveListMember {
                $key="$($this.AccountName)/$($this.ListName)"
                $script:fakeMembers[$key].Remove($this.Address)
                if ($script:failRemoveMemberOnce) { $script:failRemoveMemberOnce=$false; return 0 }; return 1
              }
            }
            'MEAOAM.AddressMap' {
              $o | Add-Member ScriptMethod GetAddressMap {
                if (-not $script:fakeRoutes.ContainsKey($this.SourceAddress)) { return 0 }
                $r=$script:fakeRoutes[$this.SourceAddress]; $this.Account=$r.Account; $this.DestinationAddress=$r.DestinationAddress; $this.Scope=$r.Scope; return 1
              }
              $o | Add-Member ScriptMethod AddAddressMap {
                if ($script:fakeRoutes.ContainsKey($this.SourceAddress)) { return 0 }
                $script:fakeRoutes[$this.SourceAddress]=@{ Account=$this.Account; SourceAddress=$this.SourceAddress; DestinationAddress=$this.DestinationAddress; Scope=$this.Scope }; return 1
              }
              $o | Add-Member ScriptMethod RemoveAddressMap { $script:fakeRoutes.Remove($this.SourceAddress); return 1 }
              $o | Add-Member ScriptMethod FindFirstAddressMap {
                if ($this.SourceAddress -ne '') { throw 'Address filters not reset.' }
                $target=$this.DestinationAddress; $po=$this.Account
                $this.Items=@($script:fakeRoutes.Keys | Where-Object { $script:fakeRoutes[$_].Account -eq $po -and $script:fakeRoutes[$_].DestinationAddress -eq $target } | Sort-Object); $this.Index=0
                if ($this.Items.Count -eq 0) { return 0 }
                $this.SourceAddress=$this.Items[0]; $this.Scope=$script:fakeRoutes[$this.SourceAddress].Scope; return 1
              }
              $o | Add-Member ScriptMethod FindNextAddressMap {
                if ($this.SourceAddress -ne '' -or $this.Scope -ne '') { throw 'Address filters not reset.' }
                $this.Index++; if ($this.Index -ge $this.Items.Count) { return 0 }
                $this.SourceAddress=$this.Items[$this.Index]; $this.Scope=$script:fakeRoutes[$this.SourceAddress].Scope; return 1
              }
            }
            default { throw 'Unexpected COM class.' }
          }
          return $o
        }
        """;
}
