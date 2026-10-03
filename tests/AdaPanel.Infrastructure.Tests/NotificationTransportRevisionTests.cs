using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Infrastructure.Support;
using MailKit.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Fixture = AdaPanel.Infrastructure.Tests.NotificationLifecycleTests.Fixture;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NotificationTransportRevisionTests
{
    private const string SettingsPath = "config/notifications.json";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("::1")]
    public async Task SmtpRebindingIsRejectedBeforeTransportOrCredentials(string address)
    {
        using var f = new Fixture(); await f.Seed();
        var service = f.Service(resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }));
        var error = await Assert.ThrowsAsync<AppException>(() => service.SendEmailAsync("subject", "body"));
        Assert.Equal("notifications.smtp_host_forbidden", error.MessageKey);
        Assert.Equal(0, f.ConnectorCalls); Assert.Equal(0, f.Smtp.Connections); Assert.Null(f.Smtp.Password);
    }

    [Fact]
    public async Task MixedPublicAndPrivateDnsDoesNotChooseAnUncheckedAddress()
    {
        using var f = new Fixture(); await f.Seed();
        var service = f.Service(resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }));
        await Assert.ThrowsAsync<AppException>(() => service.SendEmailToAsync("custom@example.net", "s", "b"));
        Assert.Equal(0, f.ConnectorCalls); Assert.Equal(0, f.Smtp.Accepted);
    }

    [Theory]
    [InlineData(465, true, SecureSocketOptions.SslOnConnect)]
    [InlineData(587, true, SecureSocketOptions.StartTls)]
    [InlineData(25, false, SecureSocketOptions.None)]
    public async Task SmtpUsesOneCheckedStreamWithTheOriginalTlsHostname(int port, bool ssl, SecureSocketOptions options)
    {
        using var f = new Fixture(); await f.Seed();
        var settings = await f.Stored(); settings.SmtpPort = port; settings.UseSsl = ssl;
        await f.Store.WriteAsync(SettingsPath, settings);
        var resolutions = 0;
        using var cts = new CancellationTokenSource();
        var service = f.Service(resolver: (host, ct) =>
        {
            Assert.Equal("smtp.example.net", host); Assert.True(ct.CanBeCanceled); resolutions++;
            return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1") });
        });
        await service.SendEmailAsync("s", "b", cts.Token);
        Assert.Equal(1, resolutions); Assert.Equal(1, f.ConnectorCalls); Assert.Equal(port, f.ConnectorPort);
        Assert.True(f.ConnectorToken.CanBeCanceled);
        Assert.Equal(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1") }, f.CheckedAddresses);
        Assert.Equal("smtp.example.net", f.Smtp.Host); Assert.Equal(options, f.Smtp.Options);
        Assert.Same(Assert.Single(f.Streams), f.Smtp.SuppliedStream);
        Assert.True(f.Streams[0].Disposed); Assert.Equal(0, f.Smtp.CertificateValidationOverrides);
        Assert.Equal(1, f.Smtp.Accepted);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    public async Task ExplicitInternalTargetPolicyStillAllowsMail(string address)
    {
        using var f = new Fixture(); await f.Seed();
        await f.Service(allowInternal: true, resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }))
            .SendEmailAsync("s", "b");
        Assert.Equal(IPAddress.Parse(address), Assert.Single(f.CheckedAddresses!));
        Assert.Equal(1, f.Smtp.Accepted); Assert.True(Assert.Single(f.Streams).Disposed);
    }

    [Fact]
    public async Task UnresolvedSmtpDoesNotOpenTransport()
    {
        using var f = new Fixture(); await f.Seed();
        var service = f.Service(resolver: (_, _) => throw new SocketException((int)SocketError.HostNotFound));
        Assert.NotNull(await Record.ExceptionAsync(() => service.SendEmailAsync("s", "b")));
        Assert.Equal(0, f.ConnectorCalls); Assert.Equal(0, f.Smtp.Accepted);
    }

    [Fact]
    public async Task CancellationDuringResolutionDoesNotOpenTransport()
    {
        using var f = new Fixture(); await f.Seed(); using var cts = new CancellationTokenSource();
        var service = f.Service(resolver: (_, ct) => { cts.Cancel(); return Task.FromCanceled<IPAddress[]>(ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendEmailAsync("s", "b", cts.Token));
        Assert.Equal(0, f.ConnectorCalls); Assert.Equal(0, f.Smtp.Accepted);
    }

    [Fact]
    public async Task ConnectorFailureDoesNotAuthenticateOrSend()
    {
        using var f = new Fixture(); await f.Seed(); var expected = new IOException("connection failed");
        var service = f.Service(connector: (_, _, _) => ValueTask.FromException<Stream>(expected));
        Assert.Same(expected, await Record.ExceptionAsync(() => service.SendEmailAsync("s", "b")));
        Assert.Equal(0, f.Smtp.Connections); Assert.Null(f.Smtp.Password); Assert.Equal(0, f.Smtp.Accepted);
    }

    [Fact]
    public async Task AResolverIgnoringCancellationCannotEnterTheConnector()
    {
        using var f = new Fixture(); await f.Seed(); using var cts = new CancellationTokenSource();
        var connectorEntries = 0;
        var service = f.Service(resolver: (_, _) =>
            { cts.Cancel(); return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }); },
            connector: (addresses, port, ct) => { connectorEntries++; return f.Connect(addresses, port, ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendEmailAsync("s", "b", cts.Token));
        Assert.Equal(0, connectorEntries); Assert.Equal(0, f.Smtp.Accepted);
    }

    [Theory]
    [InlineData("dns")]
    [InlineData("socket")]
    public async Task MailKitConnectionTimeoutAlsoBoundsTheGuardedConnection(string phase)
    {
        using var f = new Fixture(); await f.Seed(); f.Smtp.TimeoutMilliseconds = 50;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = f.Service(resolver: async (_, ct) =>
        {
            if (phase == "dns") await Task.Delay(Timeout.Infinite, ct);
            return [IPAddress.Parse("8.8.8.8")];
        }, connector: async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException("delay returned");
        });
        var error = await Record.ExceptionAsync(() => service.SendEmailAsync("s", "b", deadline.Token));
        Assert.IsType<TimeoutException>(error); Assert.False(deadline.IsCancellationRequested);
        Assert.Equal(0, f.Smtp.Connections); Assert.True(f.Smtp.Disposed);
    }

    [Fact]
    public async Task AConnectorReturningAfterTimeoutDoesNotLeakItsStreamOrStartSmtp()
    {
        using var f = new Fixture(); await f.Seed(); f.Smtp.TimeoutMilliseconds = 30;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = f.Service(connector: async (addresses, port, _) =>
        {
            await Task.Delay(150, deadline.Token);
            return await f.Connect(addresses, port, CancellationToken.None);
        });
        await Assert.ThrowsAsync<TimeoutException>(() => service.SendEmailAsync("s", "b", deadline.Token));
        Assert.True(Assert.Single(f.Streams).Disposed); Assert.Equal(0, f.Smtp.Connections); Assert.True(f.Smtp.Disposed);
    }

    [Fact]
    public async Task CallerCancellationInTheConnectorStaysCancellationNotTimeout()
    {
        using var f = new Fixture(); await f.Seed(); using var cts = new CancellationTokenSource();
        var service = f.Service(connector: (_, _, ct) => { cts.Cancel(); return ValueTask.FromCanceled<Stream>(ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendEmailAsync("s", "b", cts.Token));
        Assert.Equal(0, f.Smtp.Connections); Assert.True(f.Smtp.Disposed);
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("authenticate")]
    [InlineData("send")]
    public async Task EverySmtpFailureReleasesTheCheckedStream(string phase)
    {
        using var f = new Fixture(); await f.Seed();
        f.Smtp.FailurePhase = phase; var original = f.Smtp.Failure = new IOException("SMTP failure");
        Assert.Same(original, await Record.ExceptionAsync(() => f.Service().SendEmailAsync("s", "b")));
        Assert.True(Assert.Single(f.Streams).Disposed); Assert.True(f.Smtp.Disposed);
    }

    [Fact]
    public async Task CancellationDuringMailKitConnectReleasesTheCheckedStream()
    {
        using var f = new Fixture(); await f.Seed(); using var cts = new CancellationTokenSource();
        f.Smtp.BeforeConnect = ct => { cts.Cancel(); return Task.FromCanceled(ct); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service().SendEmailAsync("s", "b", cts.Token));
        Assert.True(Assert.Single(f.Streams).Disposed); Assert.True(f.Smtp.Disposed); Assert.Equal(0, f.Smtp.Accepted);
    }

    [Fact]
    public async Task AConnectorReturningAfterCancellationStillHasItsStreamReleased()
    {
        using var f = new Fixture(); await f.Seed(); using var cts = new CancellationTokenSource();
        var service = f.Service(connector: (addresses, port, ct) =>
        { var stream = f.Connect(addresses, port, ct); cts.Cancel(); return stream; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendEmailAsync("s", "b", cts.Token));
        Assert.True(Assert.Single(f.Streams).Disposed); Assert.Equal(0, f.Smtp.Accepted);
    }

    [Fact]
    public async Task StreamDisposeFailureDoesNotUndoAcceptedDelivery()
    {
        using var f = new Fixture(); await f.Seed();
        var service = f.Service(connector: (addresses, port, ct) =>
        { var stream = f.Connect(addresses, port, ct); f.Streams[^1].ThrowOnDispose = true; return stream; });
        await service.SendEmailAsync("s", "b");
        Assert.Equal(1, f.Smtp.Accepted); Assert.True(Assert.Single(f.Streams).Disposed);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("webhook")]
    public async Task HttpTestRequiresAReviewedRevision(string channel)
    {
        using var f = new Fixture(); await SeedRevision(f, "revision-a");
        var audit = new SahteDenetim(); var controller = Controller(f, audit);
        var error = await Assert.ThrowsAsync<AppException>(() => controller.TestAsync(new TestNotificationRequest { Channel = channel }, default));
        Assert.Equal(400, error.HttpStatusCode); Assert.Equal("notifications.settings_revision_required", error.MessageKey);
        Assert.Equal(0, f.Smtp.Accepted); Assert.Equal(0, f.Http.Requests); Assert.Empty(audit.Kayitlar);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("webhook")]
    public async Task StaleRevisionCannotSendOrProduceSuccessAudit(string channel)
    {
        using var f = new Fixture(); await SeedRevision(f, "revision-b");
        var audit = new SahteDenetim(); var controller = Controller(f, audit);
        var error = await Assert.ThrowsAsync<AppException>(() => controller.TestAsync(new TestNotificationRequest
            { Channel = channel, ExpectedSettingsRevision = "revision-a" }, default));
        Assert.Equal(409, error.HttpStatusCode); Assert.Equal("notifications.settings_changed", error.MessageKey);
        Assert.Equal(0, f.Smtp.Accepted); Assert.Equal(0, f.Http.Requests); Assert.Empty(audit.Kayitlar);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("webhook")]
    public async Task MatchingRevisionSendsTheReviewedConfiguration(string channel)
    {
        using var f = new Fixture(); await SeedRevision(f, "revision-a");
        var audit = new SahteDenetim(); var controller = Controller(f, audit);
        var response = await controller.TestAsync(new TestNotificationRequest
            { Channel = channel, ExpectedSettingsRevision = "revision-a" }, default);
        Assert.IsType<OkObjectResult>(response.Result); Assert.Single(audit.Kayitlar);
        Assert.Equal(channel == "email" ? 1 : 0, f.Smtp.Accepted); Assert.Equal(channel == "webhook" ? 1 : 0, f.Http.Requests);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("webhook")]
    public async Task TestDispatchDoesNotReadASecondDifferentConfiguration(string channel)
    {
        using var f = new Fixture(); await SeedRevision(f, "revision-a");
        var service = f.Service(beforeDispatch: async ct =>
        {
            var replacement = await f.Stored(); replacement.SettingsRevision = "revision-b";
            replacement.SmtpHost = "other-smtp.example.net"; replacement.SmtpPassword = "protected:other-secret";
            replacement.ToEmail = "other@example.net"; replacement.WebhookUrl = "https://other.example.net/hook";
            await f.Store.WriteAsync(SettingsPath, replacement, ct);
        });
        await service.TestAsync(new TestNotificationRequest { Channel = channel, ExpectedSettingsRevision = "revision-a" });
        if (channel == "email")
        { Assert.Equal("to@example.net", f.Smtp.Recipient); Assert.Equal("smtp.example.net", f.Smtp.Host); Assert.Equal("old-secret", f.Smtp.Password); }
        else Assert.Equal("https://hooks.example.net/saved", f.Http.Url);
    }

    [Fact]
    public async Task LegacyReadsReturnAStableRevisionWithoutWritingOrDisclosingCredentials()
    {
        using var f = new Fixture(); await f.Seed(); var bytes = await File.ReadAllBytesAsync(f.SettingsFile);
        var first = await f.Service().GetSettingsAsync(); var second = await f.Service().GetSettingsAsync();
        Assert.NotEmpty(first.SettingsRevision); Assert.Equal(first.SettingsRevision, second.SettingsRevision);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.SettingsFile));
        Assert.DoesNotContain("secret", first.SettingsRevision); Assert.Equal("", first.SmtpPassword);
    }

    [Fact]
    public async Task AnAbsentSettingsFileGetsARevisionWithoutCreatingTheFile()
    {
        using var f = new Fixture(); var result = await f.Service().GetSettingsAsync();
        Assert.NotEmpty(result.SettingsRevision); Assert.False(File.Exists(f.SettingsFile));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("recipient")]
    public async Task LegacyRevisionChangesWithRelevantStoredConfiguration(string field)
    {
        using var f = new Fixture(); await f.Seed(); var first = await f.Service().GetSettingsAsync();
        var settings = await f.Stored();
        if (field == "password") settings.SmtpPassword = "protected:rotated"; else settings.ToEmail = "changed@example.net";
        await f.Store.WriteAsync(SettingsPath, settings);
        Assert.NotEqual(first.SettingsRevision, (await f.Service().GetSettingsAsync()).SettingsRevision);
    }

    [Fact]
    public async Task EverySuccessfulSaveIssuesANewServerRevisionEvenForIdenticalFields()
    {
        using var f = new Fixture(); await f.Seed();
        var submitted = JsonSerializer.Deserialize<UpdateNotificationSettingsRequest>("{\"smtpPort\":587,\"settingsRevision\":\"client-forged\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var first = await f.Service().UpdateSettingsAsync(submitted); var second = await f.Service().UpdateSettingsAsync(submitted);
        Assert.True(Guid.TryParseExact(first.SettingsRevision, "N", out _));
        Assert.NotEqual(first.SettingsRevision, second.SettingsRevision); Assert.NotEqual("client-forged", second.SettingsRevision);
        Assert.Equal(second.SettingsRevision, (await f.Service().GetSettingsAsync()).SettingsRevision);
        Assert.Equal(second.SettingsRevision, (await f.Stored()).SettingsRevision);
    }

    [Fact]
    public async Task RejectedSavePreservesTheExistingRevision()
    {
        using var f = new Fixture(); await SeedRevision(f, "revision-a");
        await Assert.ThrowsAsync<AppException>(() => f.Service().UpdateSettingsAsync(new UpdateNotificationSettingsRequest { SmtpPort = -1 }));
        Assert.Equal("revision-a", (await f.Service().GetSettingsAsync()).SettingsRevision);
    }

    private static NotificationsController Controller(Fixture f, SahteDenetim audit) => new(f.Service(), audit)
    { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    private static async Task SeedRevision(Fixture f, string revision)
    { await f.Seed(); var settings = await f.Stored(); settings.SettingsRevision = revision; await f.Store.WriteAsync(SettingsPath, settings); }
}
