using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Diagnostics;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Diagnostics;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ErrorReportRevisionTests
{
    [Fact]
    public async Task LegacyReadsReturnAStableOpaqueRevisionWithoutWritingTheFile()
    {
        await using var f = await Fixture.CreateAsync();
        var bytes = await File.ReadAllBytesAsync(f.SettingsPath, f.Token);
        var timestamp = File.GetLastWriteTimeUtc(f.SettingsPath);
        using var first = f.Scope();
        using var second = f.Scope();
        var a = await first.Sender.GetSettingsAsync(f.Token);
        var b = await second.Sender.GetSettingsAsync(f.Token);
        Assert.NotEmpty(a.SettingsRevision);
        Assert.Equal(a.SettingsRevision, b.SettingsRevision);
        Assert.DoesNotContain("opaqueCollectorCredential", a.SettingsRevision);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.SettingsPath, f.Token));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(f.SettingsPath));
    }

    [Fact]
    public async Task MissingSettingsHaveAStableRevisionButAreNotCreatedByReading()
    {
        await using var f = await Fixture.CreateAsync();
        File.Delete(f.SettingsPath);
        using var scope = f.Scope();
        var first = await scope.Sender.GetSettingsAsync(f.Token);
        var second = await scope.Sender.GetSettingsAsync(f.Token);
        Assert.NotEmpty(first.SettingsRevision);
        Assert.Equal(first.SettingsRevision, second.SettingsRevision);
        Assert.False(File.Exists(f.SettingsPath));
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("collector")]
    [InlineData("consent")]
    [InlineData("batch")]
    [InlineData("retention")]
    public async Task LegacyRevisionIncludesEveryDeliverySetting(string field)
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        var first = await scope.Sender.GetSettingsAsync(f.Token);
        var changed = Fixture.Settings();
        if (field == "enabled") changed.Enabled = false;
        if (field == "collector") changed.CollectorUrl = "https://different.test/reports";
        if (field == "consent") changed.ConsentGrantedAtUtc = changed.ConsentGrantedAtUtc!.Value.AddSeconds(1);
        if (field == "batch") changed.BatchSize = 1;
        if (field == "retention") changed.RetentionDays = 1;
        await f.WriteAsync(changed);
        Assert.NotEqual(first.SettingsRevision, (await scope.Sender.GetSettingsAsync(f.Token)).SettingsRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryStatusIsNotPartOfTheSettingsRevision(bool storedRevision)
    {
        await using var f = await Fixture.CreateAsync();
        var settings = Fixture.Settings();
        settings.SettingsRevision = storedRevision ? Guid.NewGuid().ToString("N") : string.Empty;
        await f.WriteAsync(settings);
        using var scope = f.Scope();
        var first = await scope.Sender.GetSettingsAsync(f.Token);
        settings.LastError = "a measured transport failure";
        settings.LastSentAtUtc = DateTime.UtcNow;
        await f.WriteAsync(settings);
        var second = await scope.Sender.GetSettingsAsync(f.Token);
        Assert.NotEmpty(first.SettingsRevision);
        Assert.Equal(first.SettingsRevision, second.SettingsRevision);
    }

    [Fact]
    public async Task EverySaveCreatesAServerRevisionAndIgnoresTheSubmittedValue()
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        var request = Fixture.Settings();
        request.SettingsRevision = "client-chosen-revision";
        var first = await scope.Sender.UpdateSettingsAsync(request, f.Token);
        var second = await scope.Sender.UpdateSettingsAsync(request, f.Token);
        Assert.True(Guid.TryParseExact(first.SettingsRevision, "N", out _));
        Assert.True(Guid.TryParseExact(second.SettingsRevision, "N", out _));
        Assert.NotEqual(first.SettingsRevision, second.SettingsRevision);
        Assert.Equal(second.SettingsRevision, (await scope.Sender.GetSettingsAsync(f.Token)).SettingsRevision);
    }

    [Fact]
    public async Task SavingSnapshotsTheSubmittedFieldsBeforeWaitingForTheSettingsGate()
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        using var gate = await ErrorReportDeliveryLifecycle.EnterSettingsAsync(f.SettingsPath, f.Token);
        var request = Fixture.Settings();
        var save = scope.Sender.UpdateSettingsAsync(request, f.Token);
        request.Enabled = false; request.BatchSize = 1; request.RetentionDays = 1;
        request.CollectorUrl = "https://mutated.test/reports";
        gate.Dispose();
        var actual = await save;
        Assert.True(actual.Enabled);
        Assert.Equal(Fixture.Settings().CollectorUrl, actual.CollectorUrl);
        Assert.Equal(20, actual.BatchSize);
        Assert.Equal(30, actual.RetentionDays);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    public async Task ManualSendRequiresTheDisplayedRevision(string missing)
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        var request = missing == "body" ? null : new SendErrorReportsRequest
        { ExpectedSettingsRevision = missing == "null" ? null! : missing == "whitespace" ? "  " : string.Empty };
        var error = await Assert.ThrowsAsync<AppException>(() => scope.Controller.SendPendingAsync(request!, f.Token));
        Assert.Equal(400, error.HttpStatusCode);
        Assert.Equal("errorReporting.settings_revision_required", error.MessageKey);
        Assert.Equal(0, f.Posts);
        Assert.Empty(scope.Audit.Kayitlar);
    }

    [Fact]
    public async Task StaleManualRevisionNeverSendsOrWritesAnAudit()
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        await ChangedAsync(() => scope.Controller.SendPendingAsync(new SendErrorReportsRequest
        { ExpectedSettingsRevision = "outdated-revision" }, f.Token));
        Assert.Equal(0, f.Posts);
        Assert.Empty(scope.Audit.Kayitlar);
    }

    [Fact]
    public async Task TheDisplayedCurrentRevisionAllowsTheNormalManualSend()
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        var settings = await scope.Sender.GetSettingsAsync(f.Token);
        Assert.IsType<OkObjectResult>((await scope.Controller.SendPendingAsync(new SendErrorReportsRequest
        { ExpectedSettingsRevision = settings.SettingsRevision }, f.Token)).Result);
        Assert.Equal(1, f.Posts);
        Assert.Single(scope.Audit.Kayitlar);
    }

    [Fact]
    public async Task ReturningToAnEarlierConfigurationDoesNotReviveItsRevision()
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        var first = await scope.Sender.GetSettingsAsync(f.Token);
        var different = Fixture.Settings(); different.CollectorUrl = "https://different.test/reports";
        await scope.Sender.UpdateSettingsAsync(different, f.Token);
        await scope.Sender.UpdateSettingsAsync(Fixture.Settings(), f.Token);
        await ChangedAsync(() => scope.Sender.SendPendingAsync(first.SettingsRevision, f.Token));
        Assert.Equal(0, f.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AQueuedManualSendCannotAdoptASettingsSaveWhileTrustedAutomationUsesCurrentSettings(bool trusted)
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        var first = await scope.Sender.GetSettingsAsync(f.Token);
        using var gate = await ErrorReportDeliveryLifecycle.EnterSendAsync(f.SettingsPath, f.Token);
        var send = trusted ? scope.Sender.SendPendingAsync(f.Token) : scope.Sender.SendPendingAsync(first.SettingsRevision, f.Token);
        var changed = Fixture.Settings(); changed.CollectorUrl = "https://different.test/reports";
        await scope.Sender.UpdateSettingsAsync(changed, f.Token);
        gate.Dispose();
        if (trusted) { Assert.Equal(1, await send); Assert.Equal(changed.CollectorUrl, f.LastCollector); }
        else { await ChangedAsync(() => send); Assert.Equal(0, f.Posts); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASaveWhileSqlWaitsInvalidatesTheManualSnapshotEvenWhenTheValuesReturnToTheirOriginalState(bool changeBack)
    {
        await using var f = await Fixture.CreateAsync();
        var barrier = new QueryBarrier();
        using var scope = f.Scope(barrier);
        var first = await scope.Sender.GetSettingsAsync(f.Token);
        var send = scope.Sender.SendPendingAsync(first.SettingsRevision, f.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(f.Token);
            using var writer = f.Scope();
            if (changeBack)
            {
                var different = Fixture.Settings(); different.CollectorUrl = "https://different.test/reports";
                await writer.Sender.UpdateSettingsAsync(different, f.Token);
            }
            await writer.Sender.UpdateSettingsAsync(Fixture.Settings(), f.Token);
            barrier.Release.TrySetResult();
            await ChangedAsync(() => send);
            Assert.Equal(0, f.Posts);
        }
        finally { barrier.Release.TrySetResult(); await Record.ExceptionAsync(() => send); }
    }

    [Fact]
    public async Task CancelingAQueuedManualSendLeavesNoDeliveryOrRetainedGate()
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        var first = await scope.Sender.GetSettingsAsync(f.Token);
        using var gate = await ErrorReportDeliveryLifecycle.EnterSendAsync(f.SettingsPath, f.Token);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var send = scope.Sender.SendPendingAsync(first.SettingsRevision, caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        gate.Dispose();
        Assert.Equal(0, f.Posts);
        Assert.Equal(0, ErrorReportDeliveryLifecycle.SendReferences(f.SettingsPath));
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(false, 503)]
    [InlineData(true, 200)]
    [InlineData(true, 503)]
    public async Task ActualDeliveryStatusPublicationPreservesTheReviewedRevision(bool saved, int status)
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        if (saved) await scope.Sender.UpdateSettingsAsync(Fixture.Settings(), f.Token);
        var first = await scope.Sender.GetSettingsAsync(f.Token);
        f.ResponseStatus = (HttpStatusCode)status;
        if (status == 200) Assert.Equal(1, await scope.Sender.SendPendingAsync(first.SettingsRevision, f.Token));
        else await Assert.ThrowsAsync<AppException>(() => scope.Sender.SendPendingAsync(first.SettingsRevision, f.Token));
        var current = await scope.Sender.GetSettingsAsync(f.Token);
        Assert.Equal(first.SettingsRevision, current.SettingsRevision);
        if (status == 200) Assert.NotNull(current.LastSentAtUtc);
        else Assert.Equal("HTTP 503", current.LastError);
    }

    [Fact]
    public async Task CancelingASettingsSavePreservesThePersistedRevisionAndConfiguration()
    {
        await using var f = await Fixture.CreateAsync();
        using var scope = f.Scope();
        var original = await scope.Sender.UpdateSettingsAsync(Fixture.Settings(), f.Token);
        var bytes = await File.ReadAllBytesAsync(f.SettingsPath, f.Token);
        using var gate = await ErrorReportDeliveryLifecycle.EnterSettingsAsync(f.SettingsPath, f.Token);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var changed = Fixture.Settings(); changed.Enabled = false;
        var save = scope.Sender.UpdateSettingsAsync(changed, caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        gate.Dispose();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.SettingsPath, f.Token));
        Assert.Equal(original.SettingsRevision, (await scope.Sender.GetSettingsAsync(f.Token)).SettingsRevision);
    }

    private static async Task ChangedAsync(Func<Task> operation)
    {
        var error = await Assert.ThrowsAsync<AppException>(operation);
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Equal("errorReporting.settings_changed", error.MessageKey);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "adapanel-report-revision-" + Guid.NewGuid().ToString("N"));
        internal string SettingsPath => Path.Combine(Root, "config", "error-reporting.json");
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        internal CancellationToken Token => deadline.Token;
        private readonly SqliteConnection keeper = new($"Data Source=report-revision-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        internal int Posts;
        internal string? LastCollector;
        internal HttpStatusCode ResponseStatus = HttpStatusCode.OK;
        internal static ErrorReportingSettingsDto Settings() => new()
        {
            Enabled = true, CollectorUrl = "https://collector.test/reports/opaqueCollectorCredential",
            ConsentGrantedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), BatchSize = 20, RetentionDays = 30
        };
        internal static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Directory.CreateDirectory(Path.GetDirectoryName(f.SettingsPath)!);
            await f.keeper.OpenAsync(f.Token);
            using var context = f.Context();
            await context.Database.EnsureCreatedAsync(f.Token);
            await new ErrorReportService(context, NullLogger<ErrorReportService>.Instance).RecordAsync(
                new InvalidOperationException("revision fixture"), "GET /fixture", "revision-fixture", f.Token);
            await f.WriteAsync(Settings());
            return f;
        }
        internal Task WriteAsync(ErrorReportingSettingsDto value) => File.WriteAllTextAsync(SettingsPath,
            JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Token);
        private AdaPanelDbContext Context(QueryBarrier? barrier = null)
        {
            var options = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(keeper.ConnectionString);
            if (barrier is not null) options.AddInterceptors(barrier);
            return new AdaPanelDbContext(options.Options);
        }
        internal SenderScope Scope(QueryBarrier? barrier = null)
        {
            var context = Context(barrier);
            var reports = new ErrorReportService(context, NullLogger<ErrorReportService>.Instance);
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            IErrorReportSender sender = new ErrorReportSender(store, new Factory(this), reports);
            var audit = new RecordingAuditService();
            var controller = new ErrorReportsController(reports, sender, audit)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")) } }
            };
            return new SenderScope(context, sender, controller, audit);
        }
        public async ValueTask DisposeAsync()
        {
            deadline.Cancel(); deadline.Dispose(); await keeper.DisposeAsync(); Directory.Delete(Root, true);
        }
        private sealed class Factory(Fixture fixture) : IHttpClientFactory
        { public HttpClient CreateClient(string name) => new(new Handler(fixture)); }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref fixture.Posts);
                fixture.LastCollector = request.RequestUri!.AbsoluteUri;
                return Task.FromResult(new HttpResponseMessage(fixture.ResponseStatus));
            }
        }
    }
    private sealed class SenderScope(AdaPanelDbContext context, IErrorReportSender sender,
        ErrorReportsController controller, RecordingAuditService audit) : IDisposable
    {
        internal IErrorReportSender Sender => sender;
        internal ErrorReportsController Controller => controller;
        internal RecordingAuditService Audit => audit;
        public void Dispose() => context.Dispose();
    }
    private sealed class QueryBarrier : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entered;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"error_reports\"", StringComparison.Ordinal) &&
                command.CommandText.Contains("IS NULL", StringComparison.Ordinal) && Interlocked.Exchange(ref entered, 1) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
}
