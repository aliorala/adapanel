using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FtpCertificateIntegrationTests
{
    [Theory]
    [InlineData("ftp://example.test")]
    [InlineData("example.test:21")]
    [InlineData("127.0.0.1")]
    [InlineData("*.example.test")]
    [InlineData("localhost")]
    public void OnlyAnExplicitDnsNameCanSelectACertificate(string host) =>
        Assert.Throws<AppException>(() => FtpService.NormalizeCertificateHost(host));

    [Fact]
    public async Task LegacyPolicyAndUnknownSettingsSurviveVerifiedCertificateActivation()
    {
        using var f = new Fixture();
        await f.Store.WriteAsync("config/ftp-tls.json", new { requireSsl = true, retainedSetting = "keep" });
        var result = await f.Apply();
        Assert.True(result.CertificateVerified);
        Assert.Equal("WebHosting", result.CertificateStoreName);
        Assert.Equal(1, f.Writes);
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(f.Store.ResolvePath("config/ftp-tls.json")));
        Assert.True(state.RootElement.GetProperty("requireSsl").GetBoolean());
        Assert.Equal("keep", state.RootElement.GetProperty("retainedSetting").GetString());
        Assert.Equal("ftp.example.test", state.RootElement.GetProperty("hostName").GetString());
        Assert.Equal(1, f.Binding.ControlPolicy);
        Assert.Equal(0, f.Binding.DataPolicy);
    }

    [Fact]
    public async Task FailedAuthTlsDoesNotPublishSuccessAndRestoresTheOriginalCertificate()
    {
        using var f = new Fixture();
        f.ProbeWorks = false;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Apply());
        Assert.Equal("ftp.certificate_probe_failed", error.MessageKey);
        Assert.Equal("OLD", f.Binding.Thumbprint);
        Assert.Equal("My", f.Binding.StoreName);
        Assert.Equal(2, f.Writes);
        Assert.False(File.Exists(f.Store.ResolvePath("config/ftp-tls.json")));
    }

    [Fact]
    public async Task EveryListedSiteAccountUsesTheSharedVerifiedFtpName()
    {
        using var f = new Fixture();
        await f.Apply();
        f.Service.NativeAccountStatus = (_, _) => Task.FromResult<uint>(0);
        await f.Store.WriteAsync("config/ftp-users.json", new[]
        {
            new { userName = "existing_ftp", siteName = "first.example.test", homePath = "", isEnabled = true },
            new { userName = "new_ftp", siteName = "new.example.test", homePath = "", isEnabled = true }
        });
        var accounts = await f.Service.GetFtpUsersAsync();
        Assert.Equal(2, accounts.Count);
        Assert.All(accounts, account => Assert.Equal("ftp.example.test", account.ServerAddress));
    }

    [Fact]
    public async Task CertificateApplicationDoesNotSilentlyChangeAnExplicitTlsDenyPolicy()
    {
        using var f = new Fixture();
        f.Binding.DataPolicy = 2;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Apply());
        Assert.Equal("ftp.certificate_policy_blocked", error.MessageKey);
        Assert.Equal(0, f.Writes);
        Assert.Equal(2, f.Binding.DataPolicy);
    }

    [Fact]
    public async Task AForeignOrUnknownPort21ServiceCannotBeReconfigured()
    {
        using var f = new Fixture();
        f.Service.CertificateServiceReady = _ => Task.FromResult(false);
        var error = await Assert.ThrowsAsync<AppException>(() => f.Apply());
        Assert.Equal("ftp.certificate_service_unverified", error.MessageKey);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task RenewalFollowsTheExplicitFtpHostAndAppliesTheNewCertificate()
    {
        using var f = new Fixture();
        Assert.Null(await f.Service.SyncCertificateAsync(null));
        await f.Apply();
        f.Selected = new("RENEWED", "WebHosting", "CN=ftp.example.test", DateTime.UtcNow.AddDays(89));
        var result = await f.Service.SyncCertificateAsync(null);
        Assert.Equal("RENEWED", result!.CertificateThumbprint);
        Assert.Equal("ftp.example.test", f.SelectedHost);
        Assert.Equal("RENEWED", f.Binding.Thumbprint);
        Assert.Equal(2, f.Writes);
    }

    [Fact]
    public async Task ExistingMatchingCertificateIsProbedWithoutUnnecessaryServiceRestart()
    {
        using var f = new Fixture();
        f.Binding.Thumbprint = "NEW"; f.Binding.StoreName = "WebHosting";
        Assert.True((await f.Apply()).CertificateVerified);
        Assert.Equal(0, f.Writes);
        Assert.Equal(1, f.Probes);
        Assert.True((await f.Service.GetCertificateAsync()).CertificateVerified);
        Assert.Equal(2, f.Probes);
    }

    [Fact]
    public async Task RevokedAuthorityAfterSelectionPreventsNativeChangesAndStatePublication()
    {
        using var f = new Fixture();
        var revoked = false;
        f.Service.CertificateSelector = (_, _) => { revoked = true; return Task.FromResult(f.Selected); };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ApplyCertificateAsync(
            new() { HostName = "ftp.example.test" }, _ => revoked ? throw new UnauthorizedAccessException() : Task.CompletedTask));
        Assert.Equal(0, f.Writes);
        Assert.False(File.Exists(f.Store.ResolvePath("config/ftp-tls.json")));
    }

    [Fact]
    public async Task AChangedNativeBindingIsReportedRatherThanReusingSavedSuccess()
    {
        using var f = new Fixture();
        await f.Apply();
        f.Binding.Thumbprint = "EXTERNAL";
        var status = await f.Service.GetCertificateAsync();
        Assert.False(status.CertificateVerified);
        Assert.Equal("EXTERNAL", status.CertificateThumbprint);
        Assert.Equal(1, f.Probes);
    }

    [Fact]
    public void SelfSignedMachineCertificatesAreNotValidFtpsCandidates()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=machine", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("machine.example.test"); request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
        Assert.False(FtpService.IsUsableCertificate(cert, "ftp.example.test"));
        Assert.False(FtpService.IsUsableCertificate(cert, "machine.example.test"));
    }

    [Theory]
    [InlineData("220 FTP ready\r\n", 220)]
    [InlineData("220-Welcome\r\nline without code\r\n220 Ready\r\n", 220)]
    [InlineData("234 Proceed\r\n", 234)]
    public async Task FtpRepliesAreBoundedAndMultilineAware(string response, int expected)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(response));
        await FtpTlsProbe.ReadReplyAsync(stream, expected, default);
    }

    [Fact]
    public async Task AuthTls502IsAnExplicitFailureBeforeAnyCredentials()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("502 SSL/TLS authentication not allowed\r\n"));
        var error = await Assert.ThrowsAsync<IOException>(() => FtpTlsProbe.ReadReplyAsync(stream, 234, default));
        Assert.Contains("502", error.Message);
        Assert.Contains("no credentials", error.Message);
    }

    [Theory]
    [InlineData("SslAllow", "SslAllow", 0, 0)]
    [InlineData("SslRequire", "SslRequire", 1, 1)]
    [InlineData("SslRequireCredentialsOnly", "SslAllow", 2, 0)]
    [InlineData("0", "1", 0, 1)]
    [InlineData("2", "0", 2, 0)]
    public async Task ActualPowerShellReadAndCompareAndSetAcceptIisPolicyEnumNames(
        string control, string data, int expectedControl, int expectedData)
    {
        using var f = new Fixture();
        f.Binding.ControlPolicy = expectedControl;
        f.Binding.DataPolicy = expectedData;
        await f.Apply();
        var read = f.Scripts.First(script => !script.Contains("Set-ItemProperty $path", StringComparison.Ordinal));
        var write = Assert.Single(f.Scripts, script => script.Contains("Set-ItemProperty $path", StringComparison.Ordinal));
        var fake = $$"""
            $script:values = @{ controlChannelPolicy = '{{control}}'; dataChannelPolicy = '{{data}}'; serverCertHash = 'OLD'; serverCertStoreName = 'My' }
            $script:ssl = [pscustomobject]@{ Values = $script:values }
            $script:ssl | Add-Member ScriptMethod GetAttributeValue { param($name) return $this.Values[$name] }
            foreach ($name in @('controlChannelPolicy','dataChannelPolicy','serverCertHash','serverCertStoreName')) {
              $script:ssl | Add-Member ScriptProperty $name ([scriptblock]::Create('$this.GetAttributeValue("' + $name + '")'))
            }
            function Import-Module { [CmdletBinding()]param($Name) }
            function Get-Item { [CmdletBinding()]param($Path,$LiteralPath)
              if ($LiteralPath -like 'Cert:*') { return [pscustomobject]@{ HasPrivateKey = $true } }
              return [pscustomobject]@{ ftpServer = [pscustomobject]@{ security = [pscustomobject]@{ ssl = $script:ssl } } }
            }
            function Set-ItemProperty { [CmdletBinding()]param($Path,$Name,$Value)
              $property = ($Name -split '\.')[ -1 ]
              if ($property -in @('controlChannelPolicy','dataChannelPolicy')) { throw 'Certificate activation must preserve the SSL policies.' }
              $script:values[$property] = $Value
            }
            function Restart-Service { [CmdletBinding()]param($Name,[switch]$Force) }
            function Get-Service { [CmdletBinding()]param($Name)
              $service = [pscustomobject]@{}
              $service | Add-Member ScriptMethod WaitForStatus { param($state,$timeout) }
              return $service
            }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(fake + "\n" + read + "\n" + write + "\n" + read, default);
        Assert.True(result.Success, result.StandardError);
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal(expectedControl, json.RootElement.GetProperty("controlPolicy").GetInt32());
            Assert.Equal(expectedData, json.RootElement.GetProperty("dataPolicy").GetInt32());
        }
        using var after = JsonDocument.Parse(lines[1]);
        Assert.Equal("NEW", after.RootElement.GetProperty("thumbprint").GetString());
        Assert.Equal("WebHosting", after.RootElement.GetProperty("storeName").GetString());
    }

    [Theory]
    [InlineData("SslDeny", "control")]
    [InlineData("SslRequireCredentialsOnly", "data")]
    [InlineData("unrecognized", "control")]
    [InlineData("3", "data")]
    [InlineData("", "control")]
    public async Task ActualPowerShellRejectsUnknownOrWrongChannelEnumNames(string policy, string channel)
    {
        var result = await ProcessRunner.RunPowerShellAsync(FtpService.FtpSslPolicyScript
            + $"\nConvertTo-AdaFtpSslPolicy '{policy}' '{channel}'", default);
        Assert.False(result.Success);
        Assert.Contains("Unsupported FTP", result.StandardError);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ada-ftp-cert-" + Guid.NewGuid().ToString("N"));
        internal JsonFileStore Store { get; }
        internal FtpService Service { get; }
        internal FtpService.FtpCertificateBinding Binding = new() { Thumbprint = "OLD", StoreName = "My", ControlPolicy = 1, DataPolicy = 0 };
        internal FtpService.FtpCertificateCandidate Selected = new("NEW", "WebHosting", "CN=ftp.example.test", DateTime.UtcNow.AddDays(80));
        internal int Writes, Probes;
        internal List<string> Scripts { get; } = [];
        internal bool ProbeWorks = true;
        internal string? SelectedHost;
        internal Fixture()
        {
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            Store = new JsonFileStore(options);
            Service = new FtpService(null!, Store, null!, options)
            {
                CertificateServiceReady = _ => Task.FromResult(true),
                CertificateSelector = (host, _) => { SelectedHost = host; return Task.FromResult(Selected); },
                CertificateProbe = (host, thumb, _) => { Probes++; return Task.FromResult(new FtpCertificateDto
                    { HostName = host, CertificateThumbprint = thumb, CertificateVerified = ProbeWorks, CertificateDetail = ProbeWorks ? "verified" : "AUTH TLS rejected 502" }); },
                NativeScript = (script, strict, _) =>
                {
                    Assert.True(strict);
                    Scripts.Add(script);
                    if (script.Contains("Set-ItemProperty $path", StringComparison.Ordinal))
                    {
                        Assert.DoesNotContain("controlChannelPolicy -Value", script);
                        Assert.DoesNotContain("dataChannelPolicy -Value", script);
                        Binding.Thumbprint = Regex.Match(script, "serverCertHash -Value '([^']*)'").Groups[1].Value;
                        Binding.StoreName = Regex.Match(script, "serverCertStoreName -Value '([^']*)'").Groups[1].Value;
                        Writes++;
                        return Task.FromResult(new CommandResult { ExitCode = 0 });
                    }
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = JsonSerializer.Serialize(Binding) });
                }
            };
        }
        internal Task<FtpCertificateDto> Apply() => Service.ApplyCertificateAsync(new() { HostName = "ftp.example.test" }, _ => Task.CompletedTask);
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
