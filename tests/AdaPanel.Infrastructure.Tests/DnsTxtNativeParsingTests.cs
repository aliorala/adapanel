using System.Reflection;
using AdaPanel.Infrastructure.Dns;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DnsTxtNativeParsingTests
{
    private static string Functions => (string)typeof(DnsService)
        .GetField("NativeTxtFunctions", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;

    [Theory]
    [InlineData("example.test. 3600 IN TXT \"MiXeD+/=;\" \"tail\"", "MiXeD+/=;tail")]
    [InlineData("example.test. IN TXT \"quote=\\\"x\\\";slash=\\\\\"", "quote=\"x\";slash=\\")]
    [InlineData("example.test. IN TXT \"a\\032b\" \"\\065\"", "a bA")]
    [InlineData("example.test. 60 TXT \"\"", "")]
    public async Task Entire_RR_joins_only_its_own_chunks_and_preserves_escaped_characters(string representation, string expected)
    {
        var result = await Run("$v=Convert-AdaTxtRdata '" + representation.Replace("'", "''") + "'; [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($v))");
        Assert.True(result.Success, result.StandardError);
        Assert.Equal(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(expected)), result.StandardOutput.Trim());
    }

    [Theory]
    [InlineData("example.test. IN TXT unquoted")]
    [InlineData("example.test. IN TXT \"unterminated")]
    [InlineData("example.test. IN TXT \"bad\\999\"")]
    [InlineData("example.test. IN TXT \"a\"garbage")]
    [InlineData("")]
    public async Task Malformed_full_RDATA_fails_instead_of_returning_partial_or_empty_success(string representation)
    {
        var result = await Run("Convert-AdaTxtRdata '" + representation.Replace("'", "''") + "'");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Write_verification_rejects_case_changed_or_different_RR_and_accepts_complete_exact_value()
    {
        var value = "v=DKIM1; p=" + new string('A', 390) + "+/MiXeD=";
        var script = DnsService.BuildTxtLine("selector._domainkey", value, 3600);
        // Execute the actual production verifier, without running its write strategies.
        var verifier = script.Split('\n').Single(line => line.StartsWith("$adaOlcum =", StringComparison.Ordinal));
        var result = await Run("$zoneName='example.test'; $adaAd='selector._domainkey'; $adaTam='" + value + "'\n" +
            "function Get-CimInstance { [pscustomobject]@{ContainerName='example.test';OwnerName='selector._domainkey.example.test';TTL=3600;TextRepresentation=('selector._domainkey.example.test 3600 IN TXT '+$taskData)} }\n" +
            verifier + "\n$taskData='\"" + value.ToLowerInvariant() + "\"'; if($adaOlcum.Invoke()){throw 'Case changed value accepted'}\n" +
            "$taskData='\"" + value[..255] + "\" \"different\"'; if($adaOlcum.Invoke()){throw 'Wrong tail accepted'}\n" +
            "$taskData='\"" + value[..255] + "\" \"" + value[255..] + "\"'; if(-not $adaOlcum.Invoke()){throw 'Full value rejected'}");
        Assert.True(result.Success, result.StandardError);
    }

    [Fact]
    public async Task Incomplete_TXT_inventory_cannot_silently_drop_existing_native_records()
    {
        var result = await Run("function Get-DnsServerResourceRecord { [pscustomobject]@{RecordType='TXT'} }\n" +
            "function Get-CimInstance {}\nGet-AdaDnsRecords 'example.test'");
        Assert.False(result.Success);
        Assert.Contains("incomplete", result.StandardError);
    }

    [Fact]
    public async Task Full_read_failure_stops_long_TXT_publication_before_any_write_strategy()
    {
        var script = DnsService.BuildTxtLine("selector._domainkey", new string('A', 400), 3600)
            .Replace("if (-not $adaVar) {", "if (-not $adaVar) { throw 'Test forbids native fallback writes.';");
        var result = await Run("$zoneName='example.test'\nfunction Get-CimInstance { throw 'Native full read failed' }\n" +
            "function Add-DnsServerResourceRecord { throw 'Unexpected write attempted' }\n" +
            "try {\n" + script + "\nthrow 'Unexpected publication success'\n} catch { if($_.Exception.Message -notlike '*Native full read failed*'){throw} }");
        Assert.True(result.Success, result.StandardError);
    }

    private static async Task<CommandResult> Run(string body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await ProcessRunner.RunPowerShellAsync("$ErrorActionPreference='Stop'\n" + Functions + "\n" + body, timeout.Token);
    }
}
