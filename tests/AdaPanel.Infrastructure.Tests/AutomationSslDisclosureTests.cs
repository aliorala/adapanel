using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    private const string RenewalSecret = "renewal-sentinel-password";
    private const string RenewalUrlSecret = "renewal-sentinel-url";
    private const string RenewalDiagnosis = "fixture renewal refused";
    private static string RenewalDiagnostic =>
        $"{RenewalDiagnosis}; password={RenewalSecret} endpoint=https://fixture:{RenewalUrlSecret}@acme.example.test/directory";

    public static IEnumerable<object[]> SslDisclosureFailures() =>
        from trusted in new[] { false, true }
        from kind in new[] { "message", "app-details", "inner" }
        select new object[] { trusted, kind };

    [Theory]
    [MemberData(nameof(SslDisclosureFailures))]
    public async Task SslDisclosureMasksRenewalErrorsInStatusStateAndLogger(bool trusted, string kind)
    {
        using var f = new Fixture();
        await f.PrepareSsl("");
        f.SslRenewalFailure = kind switch
        {
            "app-details" => new AppException("ssl.fixture_renewal_failed", 502, 502, RenewalDiagnostic),
            "inner" => new InvalidOperationException(RenewalDiagnosis, new IOException(RenewalDiagnostic)),
            _ => new InvalidOperationException(RenewalDiagnostic)
        };
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        var status = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal("failed", status.LastSslStatus);
        Assert.NotNull(status.LastSslRunUtc);
        Assert.Equal(0, f.SslMailCommands);
        Assert.Contains(RenewalDiagnosis, status.LastSslDetails);
        if (kind == "app-details") Assert.Contains("ssl.fixture_renewal_failed", status.LastSslDetails);
        Assert.Contains(f.SslOutcomeLog.Entries, entry => entry.Contains(RenewalDiagnosis, StringComparison.Ordinal));
        AssertNoRenewalSecrets(status.LastSslDetails);
        AssertNoRenewalSecrets(Encoding.UTF8.GetString(await f.SslStateBytes()));
        AssertNoRenewalSecrets(string.Join("\n", f.SslOutcomeLog.Entries));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SslDisclosureMasksUnmeasuredProviderReasonBeforePublication(bool trusted)
    {
        using var f = new Fixture();
        await f.PrepareSsl("");
        f.SslOutcome = new RenewAllResultDto { Measured = false, FailureReason = RenewalDiagnostic };
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        var status = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal("failed", status.LastSslStatus);
        Assert.Contains(RenewalDiagnosis, status.LastSslDetails);
        Assert.Contains("acme.example.test/directory", status.LastSslDetails);
        Assert.Equal(1, f.SslMailCommands);
        AssertNoRenewalSecrets(status.LastSslDetails);
        AssertNoRenewalSecrets(Encoding.UTF8.GetString(await f.SslStateBytes()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SslDisclosurePreservesCancellationWithoutPublishingOrLoggingFailure(bool trusted)
    {
        using var f = new Fixture();
        await f.PrepareSsl("");
        var canceled = new OperationCanceledException(RenewalDiagnostic);
        f.SslRenewalFailure = canceled;
        var before = await f.SslStateBytes();
        var error = await Capture(() => trusted ? f.SslAutomation.RunSslRenewNowAsync(f.Token) : f.InvokeSsl());
        Assert.Same(canceled, error);
        Assert.Equal(before, await f.SslStateBytes());
        Assert.Equal(0, f.SslMailCommands);
        Assert.Empty(f.SslOutcomeLog.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SslDisclosureKeepsAUsefulNonSecretFailureReasonUnchanged(bool trusted)
    {
        using var f = new Fixture();
        await f.PrepareSsl("");
        const string reason = "win-acme unavailable; executable not found at C:\\fixture\\wacs.exe";
        f.SslOutcome = new RenewAllResultDto { Measured = false, FailureReason = reason };
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        Assert.Equal(reason, (await f.SslAutomation.GetStatusAsync(f.Token)).LastSslDetails);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SslDisclosureARecoveryReplacesThePreviousFailure(bool trusted)
    {
        using var f = new Fixture();
        await f.PrepareSsl("");
        f.SslRenewalFailure = new InvalidOperationException(RenewalDiagnostic);
        await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        f.SslRenewalFailure = null;
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        var status = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal("completed", status.LastSslStatus);
        Assert.Contains("2 sertifika yenilendi.", status.LastSslDetails);
        Assert.DoesNotContain(RenewalDiagnosis, status.LastSslDetails);
        AssertNoRenewalSecrets(Encoding.UTF8.GetString(await f.SslStateBytes()));
    }

    private static void AssertNoRenewalSecrets(string text)
    {
        Assert.DoesNotContain(RenewalSecret, text);
        Assert.DoesNotContain(RenewalUrlSecret, text);
    }

    private sealed partial class Fixture
    {
        internal Exception? SslRenewalFailure;
    }
}
