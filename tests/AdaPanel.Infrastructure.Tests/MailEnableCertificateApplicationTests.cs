using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
using System.Diagnostics;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailEnableCertificateApplicationTests
{
    [Fact]
    public async Task Generated_certificate_transactions_parse_in_Windows_PowerShell_without_execution()
    {
        if (!OperatingSystem.IsWindows()) return;
        var transaction = new MailEnableMailService.CertificateTransaction("ABC", "mail.one.test", "1", [], ["MESMTPC"], false, "mail.one.test", []);
        var scripts = new[] { MailEnableMailService.CertificateInventoryScript,
            MailEnableMailService.BuildCertificateApplyScript(transaction, "mail.one.test", "AA==", "public-test", @"C:\data\journal.json"),
            MailEnableMailService.BuildCertificateRollbackScript(transaction) };
        foreach (var script in scripts)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            var parser = "$source=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "')); $errors=$null; $tokens=$null; " +
                "[void][System.Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors); if($errors.Count -gt 0){ $errors | ForEach-Object Message; exit 1 }; exit 0";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe"))
                { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("-");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync(parser); process.StandardInput.Close();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await output + await error);
        }
    }

    [Fact]
    public void Renewal_selects_only_overlapping_names_and_keeps_other_default()
    {
        using var old = Certificate("mail.one.test", "mail.one.test");
        using var other = Certificate("mail.two.test", "mail.two.test");
        using var replacement = Certificate("mail.one.test", "mail.one.test");
        var plan = MailEnableMailService.PlanCertificateReplacement("mail.one.test", replacement,
            new("mail.two.test", "1", [Encode(old), Encode(other)], ["MESMTPC"]));
        Assert.Equal([old.Thumbprint], plan.Removed);
        Assert.Equal("mail.two.test", plan.AppliedDefaultName);
        Assert.False(plan.AlreadyPresent);
        Assert.False(plan.Started);
    }

    [Fact]
    public void Renewal_cannot_drop_a_shared_SAN_or_leave_a_dangling_default()
    {
        using var shared = Certificate("mail.one.test", "mail.one.test", "mail.two.test");
        using var replacement = Certificate("mail.one.test", "mail.one.test");
        Assert.Throws<AppException>(() => MailEnableMailService.PlanCertificateReplacement("mail.one.test", replacement,
            new("mail.one.test", "1", [Encode(shared)], ["MESMTPC"])));
        using var differentName = Certificate("legacy.one.test", "mail.one.test");
        Assert.Throws<AppException>(() => MailEnableMailService.PlanCertificateReplacement("mail.one.test", replacement,
            new("legacy.one.test", "1", [Encode(differentName)], ["MESMTPC"])));
    }

    [Fact]
    public void Inventory_with_unknown_SNI_or_stopped_SMTP_cannot_authorize_a_mutation()
    {
        using var certificate = Certificate("mail.one.test", "mail.one.test");
        Assert.Throws<AppException>(() => MailEnableMailService.PlanCertificateReplacement("mail.one.test", certificate, new("", "unknown", [], ["MESMTPC"])));
        Assert.Throws<AppException>(() => MailEnableMailService.PlanCertificateReplacement("mail.one.test", certificate, new("", "1", [], ["MEIMAPS"])));
    }

    [Fact]
    public void An_unknown_public_certificate_is_not_retired_but_a_tracked_renewal_is()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Test issuer", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(5));
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=mail.one.test", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("mail.one.test"); request.CertificateExtensions.Add(san.Build());
        using var old = request.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var replacement = Certificate("mail.one.test", "mail.one.test");
        var snapshot = new MailEnableMailService.CertificateSnapshot("mail.one.test", "1", [Encode(old)], ["MESMTPC"]);
        Assert.Throws<AppException>(() => MailEnableMailService.PlanCertificateReplacement("mail.one.test", replacement, snapshot));
        var allowed = MailEnableMailService.PlanCertificateReplacement("mail.one.test", replacement, snapshot, new HashSet<string> { old.Thumbprint });
        Assert.Equal([old.Thumbprint], allowed.Removed);
    }

    [Fact]
    public async Task Self_signed_material_is_rejected_before_any_platform_mutation()
    {
        using var fixture = new Fixture();
        using var certificate = Certificate("mail.one.test", "mail.one.test");
        var calls = 0;
        var service = fixture.Service(certificate, (_, _) => { calls++; return Task.FromResult("OK"); });
        await Assert.ThrowsAsync<AppException>(() => service.IssueCertificateAsync(new() { Domain = "one.test", ReuseExistingOnly = true }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Failed_real_endpoint_result_rolls_back_and_does_not_publish_managed_state()
    {
        using var fixture = new Fixture();
        using var certificate = Certificate("mail.one.test", "mail.one.test");
        var applied = false; var rolledBack = false;
        var service = fixture.Service(certificate, async (script, _) =>
        {
            if (script.Contains("certificates = @($certs)", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new MailEnableMailService.CertificateSnapshot("mail.one.test", "1", [], ["MESMTPC"]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (script.Contains("$t.started = $true", StringComparison.Ordinal))
            {
                applied = true;
                var pending = await fixture.Store.ReadAsync<MailEnableMailService.CertificateTransaction?>("config/mailenable-certificate-pending.json", null);
                await fixture.Store.WriteAsync("config/mailenable-certificate-pending.json", pending! with { Started = true });
                return "OK";
            }
            rolledBack = true; return "OK";
        }, trustForOrchestration: true);
        var error = await Assert.ThrowsAsync<AppException>(() => service.IssueCertificateAsync(new() { Domain = "one.test", ReuseExistingOnly = true }));
        Assert.Equal("mail.certificate_not_activated", error.MessageKey);
        Assert.True(applied); Assert.True(rolledBack);
        Assert.Empty(await fixture.Store.ReadAsync<MailEnableMailService.ManagedCertificate[]>(MailEnableMailService.MailEnableCertificateStatePath, []));
        Assert.Null(await fixture.Store.ReadAsync<MailEnableMailService.CertificateTransaction?>("config/mailenable-certificate-pending.json", null));
    }

    [Fact]
    public async Task Revoked_renewal_authority_prevents_even_recovery_commands()
    {
        using var fixture = new Fixture();
        var commands = 0;
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance, fixture.Store)
        { KomutKosucusu = (_, _) => { commands++; return Task.FromResult("OK"); } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SyncCertificateAsync(_ => throw new UnauthorizedAccessException()));
        Assert.Equal(0, commands);
    }

    private static string Encode(X509Certificate2 certificate) => Convert.ToBase64String(certificate.RawData);
    private static X509Certificate2 Certificate(string commonName, params string[] names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + commonName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); foreach (var name in names) san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "adapanel-me-certificate-test-" + Guid.NewGuid().ToString("N"));
        internal JsonFileStore Store { get; }
        internal Fixture() => Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = path }));
        internal MailEnableMailService Service(X509Certificate2 certificate, Func<string, CancellationToken, Task<string>> commands, bool trustForOrchestration = false)
        {
            using var key = certificate.GetRSAPrivateKey()!;
            var material = new MailCertificateMaterial(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), certificate.Thumbprint);
            return new MailEnableMailService(NullLogger<MailEnableMailService>.Instance, Store)
            {
                KomutKosucusu = commands, StoreCertificateLookup = _ => material,
                ValidateCertificateMaterial = trustForOrchestration ? (_, pem, _) => pem : MailCertificateValidation.Validate,
                EndpointProbe = (_, port, _, _) => Task.FromResult(new MailTlsEndpointDto { Port = port, Status = port == 443 ? "ok" : "fail" })
            };
        }
        public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
