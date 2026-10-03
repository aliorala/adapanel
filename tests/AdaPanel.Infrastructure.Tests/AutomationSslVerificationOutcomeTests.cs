using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    public static IEnumerable<object[]> SslVerificationProblems() =>
        from trusted in new[] { false, true }
        from issue in new[] { "missing", "unverified", "drift", "bindings-unknown", "provider-warning" }
        select new object[] { trusted, issue };

    [Theory]
    [MemberData(nameof(SslVerificationProblems))]
    public async Task SslVerificationDoesNotPublishSuccessForIncompleteCertificateOrBindingResults(bool trusted, string issue)
    {
        using var f = new Fixture();
        await f.PrepareSsl("");
        f.SslOutcome = VerifiedRenewal();
        switch (issue)
        {
            case "missing": f.SslOutcome.MissingFromStoreCount = 1; break;
            case "unverified": f.SslOutcome.UnverifiedCount = 2; break;
            case "drift": f.SslOutcome.BindingDriftCount = 4; break;
            case "bindings-unknown": f.SslOutcome.BindingsMeasured = false; break;
            case "provider-warning": f.SslOutcome.FailureReason = "fixture verification warning; password=verification-sentinel"; break;
        }
        f.SslMailResult = new MailCertificateDto { Active = true, HostName = "mail.example.test" };
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        var status = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal("completed_with_errors", status.LastSslStatus);
        var details = status.LastSslDetails;
        Assert.Contains("2 sertifika yenilendi.", details);
        Assert.Contains("Mail sertifikası (mail.example.test) güncellendi.", details);
        Assert.Contains(issue switch
        {
            "missing" => "missing=1", "unverified" => "unverified=2", "drift" => "binding-drift=4",
            "bindings-unknown" => "bindings-measured=false", _ => "fixture verification warning"
        }, details);
        Assert.DoesNotContain("verification-sentinel", details);
        using var json = System.Text.Json.JsonDocument.Parse(await f.SslStateBytes());
        Assert.Equal("completed_with_errors", json.RootElement.GetProperty("lastSslStatus").GetString());
        Assert.Equal(details, json.RootElement.GetProperty("lastSslDetails").GetString());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public async Task SslVerificationAHealthyMeasuredRoundRemainsSuccessfulEvenWhenNothingNeedsRenewing(bool trusted, int count)
    {
        using var f = new Fixture();
        await f.PrepareSsl("");
        f.SslOutcome = VerifiedRenewal();
        f.SslOutcome.RenewedCount = count;
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        Assert.Equal("completed", (await f.SslAutomation.GetStatusAsync(f.Token)).LastSslStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SslVerificationARecoveredRoundReplacesThePreviousIncompleteOutcome(bool trusted)
    {
        using var f = new Fixture();
        await f.PrepareSsl("");
        f.SslOutcome = VerifiedRenewal();
        f.SslOutcome.BindingDriftCount = 1;
        await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        Assert.Equal("completed_with_errors", (await f.SslAutomation.GetStatusAsync(f.Token)).LastSslStatus);
        f.SslOutcome = VerifiedRenewal();
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        var status = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal("completed", status.LastSslStatus);
        Assert.DoesNotContain("binding-drift=1", status.LastSslDetails);
    }

    private static RenewAllResultDto VerifiedRenewal() => new()
    { Measured = true, BindingsMeasured = true, RenewedCount = 2, VerifiedCount = 3 };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SslVerificationCannotTreatVerifiedCertificatesAsACompletedUnmeasuredRenewal(bool trusted, bool bindingsMeasured)
    {
        using var f = new Fixture();
        await f.PrepareSsl("");
        f.SslOutcome = VerifiedRenewal();
        f.SslOutcome.Measured = false;
        f.SslOutcome.BindingsMeasured = bindingsMeasured;
        f.SslOutcome.FailureReason = "fixture renewal executable unavailable";
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        var status = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal("failed", status.LastSslStatus);
        Assert.Equal("fixture renewal executable unavailable", status.LastSslDetails);
        using var json = System.Text.Json.JsonDocument.Parse(await f.SslStateBytes());
        Assert.Equal("failed", json.RootElement.GetProperty("lastSslStatus").GetString());
    }
}
