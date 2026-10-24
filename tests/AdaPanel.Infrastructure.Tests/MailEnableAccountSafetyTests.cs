using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailEnableAccountSafetyTests
{
    [Theory]
    [InlineData("delete")]
    [InlineData("password")]
    [InlineData("quota")]
    [InlineData("alias-create")]
    [InlineData("alias-delete")]
    [InlineData("autoresponder")]
    public async Task Foreign_domain_owner_blocks_every_account_mutation_before_mutating_COM_objects(string operation)
    {
        var calls = 0;
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = async (script, token) =>
            {
                calls++;
                const string fake = """
                    function New-Object {
                      param([string]$ComObject)
                      if ($ComObject -ne 'MEAOSM.Domain') { throw 'MUTATION_REACHED' }
                      $o = [pscustomobject]@{ DomainName=''; AccountName='' }
                      $o | Add-Member ScriptMethod GetDomain { $this.AccountName='neighbor.test'; return 1 }
                      return $o
                    }
                    """;
                var result = await ProcessRunner.RunPowerShellAsync(fake + "\n" + script, token);
                Assert.False(result.Success);
                Assert.Contains("SMTP domain ownership could not be verified", result.StandardError);
                Assert.DoesNotContain("MUTATION_REACHED", result.StandardError);
                throw new AppException("mail.command_failed", 502, 502);
            }
        };
        await Assert.ThrowsAsync<AppException>(() => operation switch
        {
            "delete" => service.DeleteAccountAsync("info@example.test"),
            "password" => service.SetAccountPasswordAsync(new() { Address="info@example.test", Password="test-only" }),
            "quota" => service.SetAccountQuotaAsync(new() { Address="info@example.test", MaxSizeMB=10 }),
            "alias-create" => service.CreateAliasAsync(new() { Domain="example.test", Address="alias", Target="info@example.test" }),
            "alias-delete" => service.DeleteAliasAsync("alias@example.test"),
            _ => (Task)service.SetAutoResponderAsync(new() { Address="info@example.test", Message="test-only" })
        });
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("example.test", 1, true)]
    [InlineData("EXAMPLE.TEST", 1, true)]
    [InlineData("example.test", 0, false)]
    [InlineData("", 1, false)]
    public async Task Ownership_requires_documented_success_and_matching_postoffice(string owner, int code, bool success)
    {
        var fake = $$"""
            function New-Object {
              param([string]$ComObject)
              $o=[pscustomobject]@{ DomainName=''; AccountName='{{owner}}' }
              $o | Add-Member ScriptMethod GetDomain { return {{code}} }
              return $o
            }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(fake + "\n" + MailEnableMailService.DomainOwnershipGuard("example.test") + "'OK'", default);
        Assert.Equal(success, result.Success);
        if (success) Assert.Equal("OK", result.StandardOutput.Trim());
    }

    [Theory]
    [InlineData("info", "INFO@EXAMPLE.TEST")]
    [InlineData(" INFO@EXAMPLE.TEST ", " info@example.test ")]
    public async Task Normalized_self_alias_is_rejected_before_native_calls(string source, string target)
    {
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        { KomutKosucusu = (_, _) => throw new InvalidOperationException("No native call expected") };
        var error = await Assert.ThrowsAsync<AppException>(() => service.CreateAliasAsync(new()
        { Domain="EXAMPLE.TEST", Address=source, Target=target }));
        Assert.Equal("mail.alias_self_reference", error.MessageKey);
    }
}
