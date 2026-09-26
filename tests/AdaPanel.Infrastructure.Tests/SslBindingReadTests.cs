using System.Text.Json;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslBindingReadTests
{
    [Fact]
    public async Task Actual_PowerShell_reader_handles_text_bytes_missing_and_invalid_hashes()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string fixture = """
            function Import-Module { param($Name) }
            function Get-Website {
              [pscustomobject]@{ Name='example.test'; Bindings=[pscustomobject]@{Collection=@(
                [pscustomobject]@{protocol='https';bindingInformation='*:443:text.test';certificateHash='AABB0123';certificateStoreName='My'},
                [pscustomobject]@{protocol='https';bindingInformation='*:443:bytes.test';certificateHash=[byte[]](170,187,1,35);certificateStoreName='My'},
                [pscustomobject]@{protocol='https';bindingInformation='*:443:missing.test';certificateHash=$null;certificateStoreName='My'},
                [pscustomobject]@{protocol='https';bindingInformation='*:443:invalid.test';certificateHash='not-hex';certificateStoreName='My'},
                [pscustomobject]@{protocol='https';bindingInformation='*:443:number.test';certificateHash=123;certificateStoreName='My'},
                [pscustomobject]@{protocol='http';bindingInformation='*:80:example.test'}
              )} }
            }
            """;
        var result = await ProcessRunner.RunPowerShellStrictAsync(fixture + "\n" + WinAcmeSslService.HttpsBindingsScript, default);
        Assert.True(result.Success, result.StandardError);
        var rows = JsonSerializer.Deserialize<IisHttpsBinding[]>(result.StandardOutput,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(5, rows.Length);
        Assert.All(rows.Take(2), row => { Assert.Equal("aabb0123", row.Thumbprint); Assert.True(row.CertificateReadable); });
        Assert.True(rows[2].CertificateReadable);
        Assert.Empty(rows[2].Thumbprint);
        Assert.All(rows.Skip(3), row => { Assert.False(row.CertificateReadable); Assert.Empty(row.Thumbprint); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reader_always_returns_an_array_for_zero_or_one_binding(bool single)
    {
        if (!OperatingSystem.IsWindows()) return;
        var item = single ? "[pscustomobject]@{Name='one';Bindings=[pscustomobject]@{Collection=@([pscustomobject]@{protocol='https';bindingInformation='*:443:one';certificateHash='AABB';certificateStoreName='My'})}}" : "";
        var result = await ProcessRunner.RunPowerShellStrictAsync(
            "function Import-Module { param($Name) }\nfunction Get-Website { " + item + " }\n" + WinAcmeSslService.HttpsBindingsScript, default);
        Assert.True(result.Success, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(single ? 1 : 0, json.RootElement.GetArrayLength());
    }

    [Fact]
    public void Workspace_replaces_stale_verification_and_does_not_keep_green_after_read_failure()
    {
        var record = new SslCertificateDto { SiteName = "site", HostName = "one.test", Thumbprint = "AABB", BindingDetail = "old unreadable", IsCertificateBound = null };
        SslBindingVerification.Refresh([record], [new() { SiteName = "site", BindingInformation = "[2001:db8::1]:443:one.test", Thumbprint = "aabb", CertificateReadable = true }]);
        Assert.True(record.IsHttpsBindingPresent);
        Assert.True(record.IsCertificateBound);
        Assert.Empty(record.BindingDetail);
        SslBindingVerification.Refresh([record], null);
        Assert.Null(record.IsHttpsBindingPresent);
        Assert.Null(record.IsCertificateBound);
        Assert.NotEmpty(record.BindingDetail);
    }

    [Fact]
    public void Wildcard_certificate_must_actually_be_bound_to_the_selected_site()
    {
        var record = new SslCertificateDto { SiteName = "site", HostName = "*.test", Thumbprint = "AABB" };
        SslBindingVerification.Refresh([record], [new() { SiteName = "other", BindingInformation = "*:443:one.test", Thumbprint = "aabb", CertificateReadable = true }]);
        Assert.False(record.IsCertificateBound);
        SslBindingVerification.Refresh([record], [new() { SiteName = "site", BindingInformation = "*:443:one.test", Thumbprint = "aabb", CertificateReadable = true }]);
        Assert.True(record.IsCertificateBound);
    }

    [Fact]
    public void Unknown_other_binding_prevents_shared_certificate_deletion()
    {
        var target = new IisHttpsBinding { SiteName = "site", BindingInformation = "*:443:one.test", Thumbprint = "aabb", CertificateReadable = true };
        var unknown = new IisHttpsBinding { SiteName = "other", BindingInformation = "*:443:other.test", CertificateReadable = false };
        Assert.NotEmpty(SslRemovalPlan.FindOtherUsersOfCertificate([target, unknown], target));
    }
}
