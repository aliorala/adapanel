using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailDnsAuditTests
{
    [Theory]
    [InlineData("MX", "mail.example.test", "backup.example.test", 10, 10, false)]
    [InlineData("MX", "mail.example.test", "mail.example.test", 10, 20, false)]
    [InlineData("MX", "mail.example.test", "MAIL.example.test.", 10, 10, true)]
    [InlineData("A", "192.0.2.10", "192.0.2.11", 0, 0, false)]
    [InlineData("A", "192.0.2.10", "192.0.2.10", 0, 0, true)]
    [InlineData("AAAA", "2001:db8::1", "2001:db8:0:0:0:0:0:1", 0, 0, true)]
    [InlineData("TXT", "v=DKIM1; p=AQID", "v=DKIM1; p=aqid", 0, 0, false)]
    [InlineData("TXT", "v=spf1 mx -all", "v=spf1 mx -all", 0, 0, true)]
    [InlineData("A", "", "192.0.2.10", 0, 0, false)]
    public void Cleanup_preserves_records_with_different_values_or_priorities(string type, string expected,
        string actual, int expectedPriority, int actualPriority, bool matches)
    {
        Assert.Equal(matches, MailDnsPublication.MatchesRecord(
            new MailDnsRecordDto { Type = type, Host = "mail", Value = expected, Priority = expectedPriority },
            new SiteDnsRecordDto { Type = type, Host = "MAIL.", Value = actual, Priority = actualPriority }));
    }

    [Theory]
    [InlineData("v=DKIM1; k=rsa; p=AQID", true)]
    [InlineData("k=rsa; p=AQ ID", true)]
    [InlineData("v=DKIM1; k=rsa; p=aqid", false)]
    [InlineData("v=DKIM1; p=", false)]
    [InlineData("v=DKIM1; p=AQID; p=BAUG", false)]
    [InlineData("v=DKIM1; k=ed25519; p=AQID", false)]
    [InlineData("note=v=DKIM1", false)]
    public void Dkim_requires_the_expected_public_key(string actual, bool ok)
        => Assert.Equal(ok, MailDiagnosticsRunner.CheckTxt("dkim", [actual], "v=DKIM1; k=rsa; p=AQID").Ok);

    [Theory]
    [InlineData("spf", "v=spf1 mx -all")]
    [InlineData("dmarc", "v=DMARC1; p=quarantine")]
    [InlineData("dkim", "v=DKIM1; p=AQID")]
    public void Duplicate_authentication_records_are_not_healthy(string kind, string value)
        => Assert.False(MailDiagnosticsRunner.CheckTxt(kind, [value, value], value).Ok);

    [Theory]
    [InlineData("spf", "note=v=spf1 mx -all", "v=spf1 mx -all", false)]
    [InlineData("spf", "v=spf1 +all", "v=spf1 mx -all", false)]
    [InlineData("spf", "v=spf1  mx -all", "v=spf1 mx -all", true)]
    [InlineData("dmarc", "v=DMARC1; p=invalid", "v=DMARC1; p=invalid", false)]
    [InlineData("dmarc", "v=DMARC1; p=none", "v=DMARC1; p=quarantine", false)]
    [InlineData("dmarc", "v=DMARC1; p=quarantine; rua=mailto:reports@example.test", "v=DMARC1; p=quarantine", true)]
    public void Diagnostics_compare_configured_policy(string kind, string actual, string expected, bool ok)
        => Assert.Equal(ok, MailDiagnosticsRunner.CheckTxt(kind, [actual], expected).Ok);

    [Fact]
    public async Task Cancelled_diagnostics_do_not_return_success()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MailDiagnosticsRunner.RunAsync(new(), cts.Token));
    }
}
