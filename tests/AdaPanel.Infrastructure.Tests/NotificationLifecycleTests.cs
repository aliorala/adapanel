using System.Net;
using System.Reflection;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Notifications;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MimeKit;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Real notification service and JSON publication; transports never open a socket.</summary>
public sealed class NotificationLifecycleTests
{
    private const string SettingsPath = "config/notifications.json";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlankPasswordSavePreservesConcurrentRotation(bool initiallyConfigured)
    {
        using var f = new Fixture();
        await f.Seed(initiallyConfigured ? "old-secret" : "");
        var entered = Signal(); var release = Signal();
        var staleService = f.Service(beforeWrite: async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); });
        var request = Request(); request.ToEmail = "latest@example.net";
        var saving = staleService.UpdateSettingsAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var rotation = Request(); rotation.SmtpPassword = "rotated-secret";
            await f.Service().UpdateSettingsAsync(rotation);
        }
        finally { release.TrySetResult(); }
        await saving;
        var stored = await f.Stored();
        Assert.Equal("protected:rotated-secret", stored.SmtpPassword);
        Assert.Equal("latest@example.net", stored.ToEmail);
        Assert.Equal("", (await f.Service().GetSettingsAsync()).SmtpPassword);
    }

    [Theory]
    [InlineData("smtp")]
    [InlineData("webhook")]
    public async Task AsyncTargetValidationUsesTheSubmittedSnapshot(string target)
    {
        using var f = new Fixture();
        var request = Request();
        if (target == "webhook") request.WebhookUrl = "https://hooks.example.net/original";
        var entered = Signal(); var release = Signal();
        var service = f.Service(resolver: async (_, ct) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(ct);
            return [IPAddress.Parse("8.8.8.8")];
        });
        var saving = service.UpdateSettingsAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.SmtpHost = "127.0.0.1";
        request.WebhookUrl = "http://127.0.0.1/private";
        request.ToEmail = "changed@example.net";
        request.SmtpPassword = "changed-secret";
        release.SetResult();
        var result = await saving;
        Assert.Equal("smtp.example.net", result.SmtpHost);
        Assert.Equal(target == "webhook" ? "https://hooks.example.net/original" : "", result.WebhookUrl);
        Assert.Equal("to@example.net", result.ToEmail);
        Assert.False(result.HasSmtpPassword);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task InvalidPortCannotReplaceWorkingConfiguration(int port)
    {
        using var f = new Fixture(); await f.Seed();
        var before = await File.ReadAllBytesAsync(f.SettingsFile);
        var request = Request(); request.SmtpPort = port;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service().UpdateSettingsAsync(request));
        Assert.Equal(400, error.HttpStatusCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.SettingsFile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidMailboxCannotReplaceWorkingConfiguration(bool sender)
    {
        using var f = new Fixture(); await f.Seed();
        var before = await File.ReadAllBytesAsync(f.SettingsFile);
        var request = Request();
        if (sender) request.FromEmail = "not a mailbox <"; else request.ToEmail = "not a mailbox <";
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service().UpdateSettingsAsync(request));
        Assert.Equal(400, error.HttpStatusCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.SettingsFile));
    }

    [Fact]
    public async Task BlankPasswordPreservesStoredSecretAndNeverReturnsIt()
    {
        using var f = new Fixture(); await f.Seed();
        var saved = await f.Service().UpdateSettingsAsync(Request());
        Assert.True(saved.HasSmtpPassword); Assert.Equal("", saved.SmtpPassword);
        Assert.Equal("protected:old-secret", (await f.Stored()).SmtpPassword);
        Assert.Equal("", (await f.Service().GetSettingsAsync()).SmtpPassword);
    }

    [Fact]
    public async Task ExplicitPasswordIsProtectedAndUsedBySmtp()
    {
        using var f = new Fixture(); var request = Request(); request.SmtpPassword = "new-secret";
        await f.Service().UpdateSettingsAsync(request);
        Assert.Equal("protected:new-secret", (await f.Stored()).SmtpPassword);
        await f.Service().SendEmailAsync("subject", "body");
        Assert.Equal("new-secret", f.Smtp.Password); Assert.Equal(1, f.Smtp.Accepted);
    }

    [Fact]
    public async Task EmptyChannelsCanBeSavedAndDisableSending()
    {
        using var f = new Fixture();
        await f.Service().UpdateSettingsAsync(new UpdateNotificationSettingsRequest());
        Assert.False(await f.Service().IsEmailConfiguredAsync());
        await Assert.ThrowsAsync<AppException>(() => f.Service().SendEmailAsync("s", "b"));
        await Assert.ThrowsAsync<AppException>(() => f.Service().SendWebhookAsync("s", "b"));
        Assert.Equal(0, f.Smtp.Connections); Assert.Equal(0, f.Http.Requests);
    }

    [Theory]
    [InlineData("smtp")]
    [InlineData("webhook")]
    public async Task PrivateTargetsRemainRejectedBeforeSaving(string channel)
    {
        using var f = new Fixture(); await f.Seed();
        var before = await File.ReadAllBytesAsync(f.SettingsFile);
        var request = Request();
        if (channel == "smtp") request.SmtpHost = "127.0.0.1";
        else request.WebhookUrl = "http://127.0.0.1/private";
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service().UpdateSettingsAsync(request));
        Assert.Equal(403, error.HttpStatusCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.SettingsFile));
    }

    [Fact]
    public async Task CanceledSaveDoesNotPublishOrLeaveTemporarySettings()
    {
        using var f = new Fixture(); await f.Seed(); using var cts = new CancellationTokenSource();
        var before = await File.ReadAllBytesAsync(f.SettingsFile);
        var service = f.Service(beforeWrite: _ => { cts.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UpdateSettingsAsync(Request(), cts.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(f.SettingsFile));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(f.SettingsFile)!));
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("authenticate")]
    [InlineData("send")]
    public async Task DeliveryFailureIsNotReplacedByDisconnectFailure(string phase)
    {
        using var f = new Fixture(); await f.Seed();
        var original = new IOException("original delivery failure");
        f.Smtp.FailurePhase = phase; f.Smtp.Failure = original; f.Smtp.DisconnectFailure = new IOException("QUIT failed");
        var error = await Record.ExceptionAsync(() => f.Service().SendEmailAsync("s", "b"));
        Assert.Same(original, error); Assert.Equal(0, f.Smtp.Accepted); Assert.True(f.Smtp.Disposed);
    }

    [Fact]
    public async Task SenderRejectionSurvivesDisconnectFailure()
    {
        using var f = new Fixture(); await f.Seed();
        f.Smtp.FailurePhase = "send";
        f.Smtp.Failure = new SmtpCommandException(SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxUnavailable, "5.7.1 Sender rejected");
        f.Smtp.DisconnectFailure = new IOException("QUIT failed");
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service().SendEmailAsync("s", "b"));
        Assert.Equal("notifications.smtp_sender_rejected", error.MessageKey);
        Assert.Equal(0, f.Smtp.Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedDeliveryRemainsSuccessfulWhenCleanupFails(bool cancel)
    {
        using var f = new Fixture(); await f.Seed(); using var cts = new CancellationTokenSource();
        if (cancel) f.Smtp.AfterAccept = cts.Cancel;
        else f.Smtp.DisconnectFailure = new IOException("QUIT failed after accepted mail");
        var audit = new SahteDenetim();
        var controller = new NotificationsController(f.Service(), audit)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = await controller.TestAsync(new TestNotificationRequest { Channel = "email", Message = "test body" }, cts.Token);
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, f.Smtp.Accepted); Assert.True(f.Smtp.Disposed);
        Assert.Single(audit.Kayitlar, item => item.Action == "notifications.test_sent");
    }

    [Fact]
    public async Task FailedDeliveryNeverProducesSuccessAudit()
    {
        using var f = new Fixture(); await f.Seed(); f.Smtp.FailurePhase = "send"; f.Smtp.Failure = new IOException("not accepted");
        var audit = new SahteDenetim();
        var controller = new NotificationsController(f.Service(), audit)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        await Assert.ThrowsAsync<IOException>(() => controller.TestAsync(new TestNotificationRequest { Channel = "email" }, default));
        Assert.Empty(audit.Kayitlar); Assert.Equal(0, f.Smtp.Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeCannotReplaceTheDeliveryOutcome(bool accepted)
    {
        using var f = new Fixture(); await f.Seed();
        var original = new IOException("original send failure");
        if (!accepted) { f.Smtp.FailurePhase = "send"; f.Smtp.Failure = original; }
        f.Smtp.DisposeFailure = new IOException("dispose failed");
        f.Smtp.DisconnectFailure = new IOException("disconnect failed");
        var error = await Record.ExceptionAsync(() => f.Service().SendEmailAsync("s", "b"));
        if (accepted) Assert.Null(error); else Assert.Same(original, error);
        Assert.Equal(accepted ? 1 : 0, f.Smtp.Accepted); Assert.True(f.Smtp.Disposed);
    }

    [Theory]
    [InlineData(465, true, SecureSocketOptions.SslOnConnect)]
    [InlineData(587, true, SecureSocketOptions.StartTls)]
    [InlineData(25, false, SecureSocketOptions.None)]
    public async Task NormalSmtpSendPreservesTlsAndRecipient(int port, bool ssl, SecureSocketOptions expected)
    {
        using var f = new Fixture(); await f.Seed();
        var request = Request(); request.SmtpPort = port; request.UseSsl = ssl;
        await f.Service().UpdateSettingsAsync(request);
        await f.Service().SendEmailToAsync("custom@example.net", "subject", "body");
        Assert.Equal(expected, f.Smtp.Options); Assert.Equal(port, f.Smtp.Port);
        Assert.Equal("custom@example.net", f.Smtp.Recipient); Assert.Equal("subject", f.Smtp.Subject);
        Assert.Equal("body", f.Smtp.Body); Assert.Equal(1, f.Smtp.Accepted); Assert.True(f.Smtp.Disposed);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("webhook")]
    public async Task AlreadyCanceledSendDoesNotDeliver(string channel)
    {
        using var f = new Fixture(); await f.Seed(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service().TestAsync(new TestNotificationRequest { Channel = channel }, cts.Token));
        Assert.Equal(0, f.Smtp.Accepted); Assert.Equal(0, f.Http.Requests);
    }

    [Fact]
    public async Task WebhookUsesGuardedClientAndExpectedBody()
    {
        using var f = new Fixture(); await f.Seed();
        await f.Service().SendWebhookAsync("backup.completed", "body");
        Assert.Equal(OutboundConnectGuard.GuardedClientName, f.Http.ClientName);
        Assert.Equal("https://hooks.example.net/saved", f.Http.Url);
        using var payload = JsonDocument.Parse(f.Http.Body!);
        Assert.Equal("backup.completed", payload.RootElement.GetProperty("eventName").GetString());
        Assert.Equal("body", payload.RootElement.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(302)]
    [InlineData(400)]
    [InlineData(503)]
    public async Task WebhookNonSuccessDoesNotReportDelivery(int status)
    {
        using var f = new Fixture(); await f.Seed(); f.Http.Status = (HttpStatusCode)status;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service().SendWebhookAsync("event", "body"));
        Assert.Equal("notifications.webhook_failed", error.MessageKey); Assert.Equal(1, f.Http.Requests);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static UpdateNotificationSettingsRequest Request() => new()
    { SmtpHost = "smtp.example.net", SmtpPort = 587, SmtpUserName = "smtp-user", FromEmail = "from@example.net", ToEmail = "to@example.net", UseSsl = true };

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-notifications-" + Guid.NewGuid().ToString("N"));
        internal readonly JsonFileStore Store;
        internal readonly SmtpTransport Smtp = new();
        internal readonly HttpTransport Http = new();
        internal string SettingsFile => Store.ResolvePath(SettingsPath);
        internal Fixture() => Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        internal NotificationService Service(Func<CancellationToken, Task>? beforeWrite = null, OutboundAddressGuard.OutboundHostResolver? resolver = null) => new(
            Store, Http, new Protector(), outboundResolver: resolver ?? ((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })))
            { SmtpClientFactory = Smtp.Create, BeforeSettingsWrite = beforeWrite };
        internal Task Seed(string password = "old-secret") => Store.WriteAsync(SettingsPath, new NotificationSettingsDto
        { SmtpHost = "smtp.example.net", SmtpPort = 587, SmtpUserName = "smtp-user", SmtpPassword = password.Length == 0 ? "" : "protected:" + password,
            FromEmail = "from@example.net", ToEmail = "to@example.net", UseSsl = true, WebhookUrl = "https://hooks.example.net/saved" });
        internal Task<NotificationSettingsDto> Stored() => Store.ReadAsync(SettingsPath, new NotificationSettingsDto());
        public void Dispose() { Http.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Protector : ISecretProtector
    {
        public string Protect(string value) => "protected:" + value;
        public string Unprotect(string value) => value.StartsWith("protected:", StringComparison.Ordinal) ? value[10..] : throw new InvalidOperationException("unrecognized protected value");
    }
    private sealed class HttpTransport : HttpMessageHandler, IHttpClientFactory
    {
        internal string? ClientName, Url, Body;
        internal int Requests;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) { ClientName = name; return new HttpClient(this, false); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests++; Url = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(ct); return new HttpResponseMessage(Status);
        }
    }
    private sealed class SmtpTransport
    {
        internal string? FailurePhase, Password, Recipient, Subject, Body;
        internal Exception? Failure, DisconnectFailure, DisposeFailure;
        internal Action? AfterAccept;
        internal bool Connected, Disposed;
        internal int Connections, Accepted, Port;
        internal SecureSocketOptions Options;
        internal ISmtpClient Create()
        { var client = DispatchProxy.Create<ISmtpClient, SmtpProxy>(); ((SmtpProxy)(object)client).Call = Invoke; return client; }
        private object? Invoke(MethodInfo method, object?[] args)
        {
            if (method.Name == "get_IsConnected") return Connected;
            if (method.Name == "Dispose")
            { Disposed = true; Connected = false; if (DisposeFailure is not null) throw DisposeFailure; return null; }
            if (method.Name == "DisconnectAsync")
            {
                var ct = (CancellationToken)args[1]!;
                return ct.IsCancellationRequested ? Task.FromCanceled(ct) : DisconnectFailure is not null ? Task.FromException(DisconnectFailure) : Disconnect();
            }
            var token = args.OfType<CancellationToken>().Single(); token.ThrowIfCancellationRequested();
            if (method.Name == "ConnectAsync")
            {
                Connections++; Port = (int)args[1]!; Options = (SecureSocketOptions)args[2]!;
                if (FailurePhase == "connect") return Task.FromException(Failure!);
                Connected = true; return Task.CompletedTask;
            }
            if (method.Name == "AuthenticateAsync")
            { Password = (string)args[1]!; return FailurePhase == "authenticate" ? Task.FromException(Failure!) : Task.CompletedTask; }
            if (method.Name == "SendAsync")
            {
                if (FailurePhase == "send") return Task.FromException<string>(Failure!);
                var message = (MimeMessage)args[0]!;
                Recipient = message.To.Mailboxes.Single().Address; Subject = message.Subject; Body = message.TextBody;
                Accepted++; AfterAccept?.Invoke(); return Task.FromResult("250 accepted");
            }
            throw new NotSupportedException(method.Name);
        }
        private Task Disconnect() { Connected = false; return Task.CompletedTask; }
    }
    public class SmtpProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args ?? []);
    }
}
