using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailEnableIntegrationTests
{
    [Theory]
    [InlineData("*")]
    [InlineData("info?")]
    [InlineData("../info")]
    [InlineData("info@other.test")]
    [InlineData("info\r\nother")]
    public void Mailbox_identifiers_cannot_be_COM_wildcards_or_escape_the_selected_domain(string address)
        => Assert.Throws<AppException>(() => MailEnableMailService.ValidateLocalAddress(address, "example.test", false));

    [Fact]
    public void Catchall_is_explicit_and_regular_addresses_still_work()
    {
        Assert.Equal("*", MailEnableMailService.ValidateLocalAddress("*@example.test", "example.test", true));
        Assert.Equal("first.last+test", MailEnableMailService.ValidateLocalAddress("first.last+test@example.test", "example.test", false));
        Assert.Throws<AppException>(() => MailEnableMailService.ValidateDomain("*.example.test"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"domains\":[],\"accounts\":[]}")]
    public async Task Incomplete_inventory_is_never_configured_or_measured(string json)
    {
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        { KomutKosucusu = (_, _) => Task.FromResult(json) };
        var workspace = await service.GetWorkspaceAsync("example.test");
        Assert.False(workspace.Configured);
        Assert.False(workspace.AccountsMeasured);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_quota_does_not_execute_any_mutation(int quota)
    {
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        { KomutKosucusu = (_, _) => throw new InvalidOperationException("Must not run") };
        await Assert.ThrowsAsync<AppException>(() => service.CreateAccountAsync(new() { Domain = "example.test", Address = "info", MaxSizeMB = quota }));
        await Assert.ThrowsAsync<AppException>(() => service.SetAccountQuotaAsync(new() { Address = "info@example.test", MaxSizeMB = quota }));
    }

    [Fact]
    public void Client_candidates_exclude_MX_port_disabled_listeners_and_non_authenticated_extra_ports()
    {
        var settings = new Dictionary<string, string>
        {
            ["smtpListenPort"] = "25", ["smtpListenPortSSLEnabled"] = "0",
            ["smtpAlternatePortEnabled"] = "1", ["smtpAlternatePort"] = "587", ["smtpAlternatePortSSLEnabled"] = "0",
            ["imapListenPort"] = "143", ["imapListenPortSSLEnabled"] = "0",
            ["imapAlternatePortEnabled"] = "0", ["imapAlternatePort"] = "993", ["imapAlternatePortSSLEnabled"] = "1",
            ["smtpAdditionalPorts"] = "465,1,1,0;2525,0,0,0;2526,1,0,1;65536,1,1,0;2527,1,0,99"
        };
        var ports = MailEnableMailService.ClientCandidates(settings, "mail.example.test").Select(s => s.Port).Order().ToArray();
        Assert.Equal(new[] { 143, 465, 587 }, ports);
    }

    [Fact]
    public void Vendor_release_feed_stays_in_installed_major_and_rejects_unknown_format()
    {
        const string notes = "11.01 19th October 2026\n10.61 19th October 2026\n10.60 1st October 2026\n10.58 20th May 2026\n";
        Assert.Equal("10.61", AdaPanel.Infrastructure.Bootstrap.ComponentBootstrapper.ParseMailEnableVersion(notes, "10.58"));
        Assert.Null(AdaPanel.Infrastructure.Bootstrap.ComponentBootstrapper.ParseMailEnableVersion("Error: login required", "10.58"));
    }
    [Theory]
    [InlineData(true, true, false, true, "mailenable")]
    [InlineData(true, true, true, false, "stalwart")]
    [InlineData(false, true, false, false, "mailenable")]
    [InlineData(true, false, false, false, "stalwart")]
    public void Running_engine_wins_over_stale_installation(bool sw, bool me, bool swRunning, bool meRunning, string expected)
        => Assert.Equal(expected, MailProviderSelection.Resolve(null, sw, me, swRunning, meRunning));

    [Fact]
    public void Ambiguous_or_conflicting_engines_are_never_silently_switched()
    {
        Assert.Throws<AppException>(() => MailProviderSelection.Resolve(null, true, true, false, false));
        Assert.Throws<AppException>(() => MailProviderSelection.Resolve(null, true, true, true, true));
        Assert.Throws<AppException>(() => MailProviderSelection.Resolve("mailenable", true, true, true, false));
        Assert.Equal("mailenable", MailProviderSelection.Resolve("mailenable", true, true, false, false));
    }

    [Fact]
    public void Webmail_uses_exact_host_and_refuses_arbitrary_tenant_selection()
    {
        MailEnableWebmail.Endpoint[] endpoints = [new("ME", "webmail.one.test", 443, "/Mondo/lang/sys/login.aspx"),
            new("ME", "webmail.two.test", 443, "/Mondo/lang/sys/login.aspx")];
        Assert.Equal("webmail.two.test", MailEnableWebmail.Select(endpoints, "webmail.two.test").Host);
        Assert.Throws<AppException>(() => MailEnableWebmail.Select(endpoints, "panel.other.test"));
        Assert.Throws<AppException>(() => MailEnableWebmail.Select([], "panel.other.test"));
    }

    [Fact]
    public async Task Failed_SMTP_TLS_never_becomes_success_because_web_certificate_is_valid()
    {
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        {
            KomutKosucusu = (_, _) => Task.FromResult("""{"smtpHostName":"mail","smtpLocalDomainName":"example.test","sysDefaultCertificate":"mail.example.test","sysSNI":"1"}"""),
            EndpointProbe = (host, port, _, _) => Task.FromResult(new MailTlsEndpointDto
            { Port = port, Status = port == 465 ? "fail" : "ok", Detail = port == 465 ? "UntrustedRoot" : "" })
        };
        var result = await service.GetCertificateStatusAsync();
        Assert.False(result.Active);
        Assert.Equal("mail.example.test", result.HostName);
        Assert.Equal("ok", result.WebCertificate!.Status);
        Assert.Contains(result.Endpoints, e => e.Port == 465 && e.Status == "fail");
    }

    [Fact]
    public async Task Command_failure_does_not_claim_measured_listeners()
    {
        var service = new MailEnableMailService(NullLogger<MailEnableMailService>.Instance)
        { KomutKosucusu = (_, _) => throw new AppException("mail.command_failed", 500, 500) };
        await Assert.ThrowsAsync<AppException>(() => service.GetListenersAsync());
    }

    [Fact]
    public void Queue_parses_vendor_envelopes_and_local_epoch_without_exposing_body()
    {
        var result = MailEnableQueue.Parse("abc.MAI", "Sender=[SMTP:sender@example.test]\r\nRecipients=[SMTP:a@example.test];[SMTP:b@example.test]\r\nRetries=3\r\nStatus=Unsent\r\nTimeAcquired=1700000000\r\nNextSendTime=1700003600", TimeZoneInfo.Utc);
        Assert.Equal("sender@example.test", result.ReturnPath);
        Assert.Equal(2, result.Recipients.Count);
        Assert.All(result.Recipients, r => Assert.Equal(3, r.RetryCount));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), result.CreatedAtUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700003600), result.NextRetryUtc);
    }

    [Fact]
    public void Queue_count_is_not_limited_and_deletion_removes_only_selected_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-me-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Messages"));
        try
        {
            foreach (var id in new[] { "one.MAI", "two.MAI" })
            {
                File.WriteAllText(Path.Combine(root, id), "Recipients=[SMTP:a@example.test]\nStatus=Unsent");
                File.WriteAllText(Path.Combine(root, "Messages", id), "private body");
            }
            var queue = new MailEnableQueue(() => root);
            Assert.Equal(2, queue.Read(1).TotalCount);
            Assert.Single(queue.Read(1).Messages);
            Assert.Throws<AppException>(() => queue.ReadOne("../outside.MAI"));
            Assert.Throws<AppException>(() => queue.Delete("one.MAI:stream"));
            Assert.True(queue.Delete("one.MAI"));
            Assert.False(File.Exists(Path.Combine(root, "Messages", "one.MAI")));
            Assert.True(File.Exists(Path.Combine(root, "two.MAI")));
            Assert.False(queue.Delete("one.MAI"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Unavailable_queue_is_not_reported_empty()
        => Assert.Throws<AppException>(() => new MailEnableQueue(() => null).Read(10));

    [Fact]
    public void Sending_message_is_not_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-me-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "busy.MAI"), "Recipients=[SMTP:a@example.test]\nStatus=Sending");
            Assert.Throws<AppException>(() => new MailEnableQueue(() => root).Delete("busy.MAI"));
            Assert.True(File.Exists(Path.Combine(root, "busy.MAI")));
        }
        finally { Directory.Delete(root, true); }
    }
}
