using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Automation;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    public static IEnumerable<object[]> SslMailFailures() =>
        from trusted in new[] { false, true }
        from measured in new[] { false, true }
        from kind in new[] { "upload", "reload" }
        select new object[] { trusted, measured, kind };

    [Theory]
    [MemberData(nameof(SslMailFailures))]
    public async Task SslOutcomeDoesNotHideFailedMailUploadOrActivation(bool trusted, bool measured, string kind)
    {
        using var f = new Fixture();
        await f.PrepareSslOutcome(measured, kind);
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        var status = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal(measured ? "completed_with_errors" : "failed", status.LastSslStatus);
        Assert.NotNull(status.LastSslRunUtc);
        Assert.Contains("Mail certificate synchronization failed:", status.LastSslDetails);
        Assert.Contains(kind == "reload" ? "mail.certificate_not_activated" : "fixture upload failed", status.LastSslDetails);
        if (kind == "reload") Assert.Contains("fixture reload refused", status.LastSslDetails);
        if (!measured) Assert.Contains("fixture win-acme unavailable", status.LastSslDetails);
        else Assert.Contains("2 sertifika yenilendi.", status.LastSslDetails);
        Assert.DoesNotContain("ssl-sentinel-secret", status.LastSslDetails);
        Assert.DoesNotContain("ssl-sentinel-secret", System.Text.Encoding.UTF8.GetString(await f.SslStateBytes()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SslOutcomeKeepsTheSuccessfulMailResultAlongsideAnUnmeasuredRenewal(bool measured)
    {
        using var f = new Fixture();
        await f.PrepareSslOutcome(measured, "");
        f.SslMailResult = new MailCertificateDto { Active = true, HostName = "mail.example.test", Source = "letsencrypt" };
        await f.InvokeSsl();
        var status = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal(measured ? "completed" : "failed", status.LastSslStatus);
        Assert.Contains("Mail sertifikası (mail.example.test) güncellendi.", status.LastSslDetails);
        if (!measured) Assert.Contains("fixture win-acme unavailable", status.LastSslDetails);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SslOutcomeARecoveredSyncReplacesThePreviousFailure(bool trusted)
    {
        using var f = new Fixture();
        await f.PrepareSslOutcome(true, "reload");
        await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        Assert.Equal("completed_with_errors", (await f.SslAutomation.GetStatusAsync(f.Token)).LastSslStatus);
        f.SslMailFailure = null;
        f.SslMailResult = new MailCertificateDto { Active = true, HostName = "mail.example.test" };
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        var status = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal("completed", status.LastSslStatus);
        Assert.Contains("mail.example.test", status.LastSslDetails);
        Assert.DoesNotContain("failed", status.LastSslDetails);
        Assert.DoesNotContain("not_activated", status.LastSslDetails);
    }

    [Fact]
    public async Task SslOutcomeDoesNotSendProviderCredentialsToTheLogger()
    {
        using var f = new Fixture();
        await f.PrepareSslOutcome(true, "upload");
        await f.InvokeSsl();
        Assert.Contains(f.SslOutcomeLog.Entries, entry => entry.Contains("fixture upload failed", StringComparison.Ordinal));
        Assert.DoesNotContain("ssl-sentinel-secret", string.Join("\n", f.SslOutcomeLog.Entries));
    }

    private sealed partial class Fixture
    {
        internal RenewAllResultDto? SslOutcome;
        internal Exception? SslMailFailure;
        internal MailCertificateDto? SslMailResult;
        internal readonly SslOutcomeLogger SslOutcomeLog = new();

        internal async Task PrepareSslOutcome(bool measured, string kind)
        {
            await PrepareSsl("");
            SslOutcome = new RenewAllResultDto { Measured = measured, BindingsMeasured = true, RenewedCount = 2,
                FailureReason = measured ? "" : "fixture win-acme unavailable" };
            SslMailFailure = kind switch
            {
                "upload" => new InvalidOperationException("fixture upload failed; password=ssl-sentinel-secret"),
                "reload" => new AppException("mail.certificate_not_activated", 502, 502, "fixture reload refused"),
                _ => null
            };
        }
    }

    private sealed class SslOutcomeLogger : ILogger<AutomationService>
    {
        internal readonly List<string> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(formatter(state, exception) + exception);
    }
}
