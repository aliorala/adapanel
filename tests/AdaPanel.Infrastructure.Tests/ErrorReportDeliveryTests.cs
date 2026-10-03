using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Diagnostics;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Diagnostics;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ErrorReportDeliveryTests
{
    [Theory]
    [InlineData("disabled")]
    [InlineData("collector")]
    [InlineData("batch")]
    [InlineData("retention")]
    public async Task SettingsChangedDuringPendingSqlReadStopTheOldSubmission(string change)
    {
        await using var f = await Fixture.CreateAsync();
        var barrier = new SqlBarrier("pending");
        using var sender = f.CreateSender(barrier);
        var send = sender.Sender.SendPendingAsync(f.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(f.Token);
            var settings = await sender.Sender.GetSettingsAsync(f.Token);
            if (change == "disabled") settings.Enabled = false;
            if (change == "collector") settings.CollectorUrl = "https://new-collector.test/reports";
            if (change == "batch") settings.BatchSize = 1;
            if (change == "retention") settings.RetentionDays = 1;
            await f.SaveAsync(settings);
            barrier.Release.TrySetResult();
            var error = await Assert.ThrowsAsync<AppException>(() => send);
            Assert.Equal("errorReporting.settings_changed", error.MessageKey);
            Assert.Empty(f.Requests);
            Assert.Null(Assert.Single(await f.RowsAsync()).SentAtUtc);
        }
        finally { barrier.Release.TrySetResult(); await Record.ExceptionAsync(() => send); }
    }

    [Fact]
    public async Task AnUnchangedSettingsSaveDuringSqlReadStillAllowsDelivery()
    {
        await using var f = await Fixture.CreateAsync();
        var barrier = new SqlBarrier("pending");
        using var sender = f.CreateSender(barrier);
        var send = sender.Sender.SendPendingAsync(f.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(f.Token);
            await f.SaveAsync(await sender.Sender.GetSettingsAsync(f.Token));
            barrier.Release.TrySetResult();
            Assert.Equal(1, await send);
            Assert.Single(f.Requests);
            Assert.NotNull(Assert.Single(await f.RowsAsync()).SentAtUtc);
        }
        finally { barrier.Release.TrySetResult(); await Record.ExceptionAsync(() => send); }
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("missing")]
    public async Task SendingWithoutConsentOrCollectorNeverPosts(string reason)
    {
        await using var f = await Fixture.CreateAsync();
        using var sender = f.CreateSender();
        var settings = await sender.Sender.GetSettingsAsync(f.Token);
        if (reason == "disabled") settings.Enabled = false;
        else settings.CollectorUrl = string.Empty;
        await f.SaveAsync(settings);
        Assert.Equal(0, await sender.Sender.SendPendingAsync(f.Token));
        Assert.Empty(f.Requests);
        Assert.Null(Assert.Single(await f.RowsAsync()).SentAtUtc);
    }

    [Fact]
    public async Task TwoScopedSendersDoNotSubmitTheSameSnapshotTwice()
    {
        await using var f = await Fixture.CreateAsync();
        var network = new Boundary();
        var firstResponse = 0;
        f.Response = async (_, token) =>
        {
            if (Interlocked.Increment(ref firstResponse) == 1) await network.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        using var first = f.CreateSender();
        using var second = f.CreateSender();
        var sendFirst = first.Sender.SendPendingAsync(f.Token);
        Task<int>? sendSecond = null;
        try
        {
            await network.Entered.Task.WaitAsync(f.Token);
            sendSecond = second.Sender.SendPendingAsync(f.Token);
            // Old behavior reaches the second actual SQLite snapshot. With serialization,
            // the second sender is instead registered as a waiter before its SQL read.
            while (!second.Reports.SnapshotRead.Task.IsCompleted && ErrorReportDeliveryLifecycle.SendReferences(f.SettingsPath) < 2)
                await Task.Delay(1, f.Token);
            network.Release.TrySetResult();
            var counts = await Task.WhenAll(sendFirst, sendSecond);
            Assert.Equal(1, counts.Sum());
            Assert.Single(f.Requests);
            Assert.NotNull(Assert.Single(await f.RowsAsync()).SentAtUtc);
            Assert.Equal(0, ErrorReportDeliveryLifecycle.SendReferences(f.SettingsPath));
        }
        finally
        {
            network.Release.TrySetResult();
            await Record.ExceptionAsync(() => sendFirst);
            if (sendSecond is not null) await Record.ExceptionAsync(() => sendSecond);
        }
    }

    [Fact]
    public async Task ARepeatAfterDeliveryBecomesPendingAgain()
    {
        await using var f = await Fixture.CreateAsync();
        using (var first = f.CreateSender()) Assert.Equal(1, await first.Sender.SendPendingAsync(f.Token));
        await f.RecordAsync();
        var row = Assert.Single(await f.RowsAsync());
        Assert.Equal(2, row.OccurrenceCount);
        Assert.Null(row.SentAtUtc);
        using var second = f.CreateSender();
        Assert.Equal(1, await second.Sender.SendPendingAsync(f.Token));
        Assert.Equal(new[] { 1, 2 }, f.Requests.Select(request => Assert.Single(request.Payload.Reports).OccurrenceCount));
    }

    [Fact]
    public async Task ARepeatDuringHttpWaitIsNotAcknowledgedAsPartOfTheOldSnapshot()
    {
        await using var f = await Fixture.CreateAsync();
        var network = new Boundary();
        f.Response = async (_, token) => { await network.WaitAsync(token); return new HttpResponseMessage(HttpStatusCode.OK); };
        using var sender = f.CreateSender();
        var send = sender.Sender.SendPendingAsync(f.Token);
        try
        {
            await network.Entered.Task.WaitAsync(f.Token);
            await f.RecordAsync();
            network.Release.TrySetResult();
            Assert.Equal(1, await send);
            Assert.Equal(1, Assert.Single(Assert.Single(f.Requests).Payload.Reports).OccurrenceCount);
            var row = Assert.Single(await f.RowsAsync());
            Assert.Equal(2, row.OccurrenceCount);
            Assert.Null(row.SentAtUtc);
        }
        finally { network.Release.TrySetResult(); await Record.ExceptionAsync(() => send); }
    }

    [Fact]
    public async Task ARepeatImmediatelyBeforeSqlAcknowledgmentRemainsPending()
    {
        await using var f = await Fixture.CreateAsync();
        var barrier = new SqlBarrier("mark");
        using var context = f.Context(barrier);
        IErrorReportService reports = Fixture.Reports(context);
        var snapshot = await reports.GetUnsentAsync(20, f.Token);
        var mark = reports.MarkSentAsync(snapshot, f.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(f.Token);
            await f.RecordAsync();
            barrier.Release.TrySetResult();
            await mark;
            var row = Assert.Single(await f.RowsAsync());
            Assert.Equal(2, row.OccurrenceCount);
            Assert.Null(row.SentAtUtc);
        }
        finally { barrier.Release.TrySetResult(); await Record.ExceptionAsync(() => mark); }
    }

    [Fact]
    public async Task ConcurrentRecordUpdatesDoNotLoseAnOccurrence()
    {
        await using var f = await Fixture.CreateAsync();
        var firstBarrier = new SqlBarrier("record");
        var secondBarrier = new SqlBarrier("record");
        using var firstContext = f.Context(firstBarrier);
        using var secondContext = f.Context(secondBarrier);
        var first = f.RecordAsync(Fixture.Reports(firstContext));
        var second = f.RecordAsync(Fixture.Reports(secondContext));
        try
        {
            await Task.WhenAll(firstBarrier.Entered.Task, secondBarrier.Entered.Task).WaitAsync(f.Token);
            firstBarrier.Release.TrySetResult();
            await first;
            secondBarrier.Release.TrySetResult();
            await second;
            Assert.Equal(3, Assert.Single(await f.RowsAsync()).OccurrenceCount);
        }
        finally
        {
            firstBarrier.Release.TrySetResult(); secondBarrier.Release.TrySetResult();
            await Task.WhenAll(first, second);
        }
    }

    [Fact]
    public async Task ConfirmedDeliveryIsMarkedEvenIfTheCallerCancelsBeforeTheDatabaseWrite()
    {
        await using var f = await Fixture.CreateAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        using var sender = f.CreateSender();
        sender.Reports.BeforeMark = caller.Cancel;
        var result = await sender.Sender.SendPendingAsync(caller.Token);
        Assert.Equal(1, result);
        Assert.Single(f.Requests);
        Assert.NotNull(Assert.Single(await f.RowsAsync()).SentAtUtc);
        Assert.NotNull((await sender.Sender.GetSettingsAsync(f.Token)).LastSentAtUtc);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(503)]
    public async Task AnOldCollectorsResponseDoesNotOverwriteTheNewCollectorsStatus(int status)
    {
        await using var f = await Fixture.CreateAsync();
        var network = new Boundary();
        f.Response = async (_, token) => { await network.WaitAsync(token); return new HttpResponseMessage((HttpStatusCode)status); };
        using var sender = f.CreateSender();
        var send = sender.Sender.SendPendingAsync(f.Token);
        try
        {
            await network.Entered.Task.WaitAsync(f.Token);
            var changed = await sender.Sender.GetSettingsAsync(f.Token);
            changed.CollectorUrl = "https://new-collector.test/reports";
            await f.SaveAsync(changed);
            network.Release.TrySetResult();
            if (status == 200) Assert.Equal(1, await send);
            else Assert.Equal("errorReporting.send_failed", (await Assert.ThrowsAsync<AppException>(() => send)).MessageKey);
            var actual = await sender.Sender.GetSettingsAsync(f.Token);
            Assert.Equal(changed.CollectorUrl, actual.CollectorUrl);
            Assert.Null(actual.LastSentAtUtc);
            Assert.Empty(actual.LastError);
        }
        finally { network.Release.TrySetResult(); await Record.ExceptionAsync(() => send); }
    }

    [Fact]
    public async Task FailedDeliveryLeavesTheReportPendingAndStoresOnlyMaskedDiagnostics()
    {
        await using var f = await Fixture.CreateAsync();
        f.Response = (_, _) => throw new HttpRequestException("transport password=FixtureSecret123; Authorization: Bearer FixtureBearer456");
        using var sender = f.CreateSender();
        await Assert.ThrowsAsync<AppException>(() => sender.Sender.SendPendingAsync(f.Token));
        Assert.Null(Assert.Single(await f.RowsAsync()).SentAtUtc);
        var settings = await sender.Sender.GetSettingsAsync(f.Token);
        Assert.NotEmpty(settings.LastError);
        Assert.DoesNotContain("FixtureSecret123", settings.LastError);
        Assert.DoesNotContain("FixtureBearer456", settings.LastError);
    }

    [Fact]
    public async Task CancellationWhileWaitingForPendingSqlDoesNotPostOrMark()
    {
        await using var f = await Fixture.CreateAsync();
        var barrier = new SqlBarrier("pending");
        using var sender = f.CreateSender(barrier);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var send = sender.Sender.SendPendingAsync(caller.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(f.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
            Assert.Empty(f.Requests);
            Assert.Null(Assert.Single(await f.RowsAsync()).SentAtUtc);
        }
        finally { barrier.Release.TrySetResult(); await Record.ExceptionAsync(() => send); }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    public async Task CollectorSubmissionUsesTheConnectionGuard(string resolvedAddress)
    {
        await using var f = await Fixture.CreateAsync();
        f.GuardedAddress = IPAddress.Parse(resolvedAddress);
        using var sender = f.CreateSender();
        var error = await Assert.ThrowsAsync<AppException>(() => sender.Sender.SendPendingAsync(f.Token));
        Assert.Equal("errorReporting.collector_unreachable", error.MessageKey);
        Assert.Equal(1, f.GuardResolutions);
        Assert.Empty(f.Requests);
        Assert.Null(Assert.Single(await f.RowsAsync()).SentAtUtc);
    }

    [Fact]
    public async Task ARepeatAfterPurgeSelectionIsNotDeleted()
    {
        await using var f = await Fixture.CreateAsync();
        using (var seed = f.Context())
        {
            await seed.ErrorReports.ExecuteUpdateAsync(update => update
                .SetProperty(report => report.SentAtUtc, (DateTime?)DateTime.UtcNow.AddDays(-40))
                .SetProperty(report => report.LastSeenAtUtc, DateTime.UtcNow.AddDays(-40)), f.Token);
        }
        var barrier = new SqlBarrier("purge");
        using var context = f.Context(barrier);
        var purge = Fixture.Reports(context).PurgeAsync(30, f.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(f.Token);
            await f.RecordAsync();
            barrier.Release.TrySetResult();
            Assert.Equal(0, await purge);
            var row = Assert.Single(await f.RowsAsync());
            Assert.Equal(2, row.OccurrenceCount);
            Assert.Null(row.SentAtUtc);
        }
        finally { barrier.Release.TrySetResult(); await Record.ExceptionAsync(() => purge); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "adapanel-error-delivery-" + Guid.NewGuid().ToString("N"));
        internal string SettingsPath => Path.Combine(Root, "config", "error-reporting.json");
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        internal CancellationToken Token => deadline.Token;
        private readonly string connectionString = "Data Source=error-delivery-" + Guid.NewGuid().ToString("N") + ";Mode=Memory;Cache=Shared";
        private SqliteConnection keeper = null!;
        internal ConcurrentQueue<Posted> Requests { get; } = new();
        internal IPAddress? GuardedAddress;
        internal int GuardResolutions;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        internal static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.Root);
            f.keeper = new SqliteConnection(f.connectionString);
            await f.keeper.OpenAsync(f.Token);
            using (var context = f.Context()) await context.Database.EnsureCreatedAsync(f.Token);
            await f.RecordAsync();
            await f.SaveAsync(new ErrorReportingSettingsDto { Enabled = true, CollectorUrl = "https://collector.test/reports" });
            Assert.Single(await f.RowsAsync());
            return f;
        }
        internal AdaPanelDbContext Context(SqlBarrier? barrier = null)
        {
            var options = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connectionString);
            if (barrier is not null) options.AddInterceptors(barrier);
            return new AdaPanelDbContext(options.Options);
        }
        internal static ErrorReportService Reports(AdaPanelDbContext context) => new(context, NullLogger<ErrorReportService>.Instance);
        internal async Task RecordAsync(IErrorReportService? reports = null)
        {
            if (reports is not null) { await reports.RecordAsync(new InvalidOperationException("fixture report"), "GET /fixture", "fixture-correlation", Token); return; }
            using var context = Context();
            await RecordAsync(Reports(context));
        }
        internal async Task<ErrorReport[]> RowsAsync()
        {
            using var context = Context();
            return await context.ErrorReports.AsNoTracking().ToArrayAsync(Token);
        }
        internal SenderScope CreateSender(SqlBarrier? barrier = null)
        {
            var context = Context(barrier);
            var reports = new ObservedReports(Reports(context));
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            return new SenderScope(context, reports, new ErrorReportSender(store, new Factory(this), reports));
        }
        internal async Task SaveAsync(ErrorReportingSettingsDto settings)
        {
            using var sender = CreateSender();
            await sender.Sender.UpdateSettingsAsync(settings, Token);
        }
        public async ValueTask DisposeAsync()
        {
            deadline.Cancel(); deadline.Dispose();
            await keeper.DisposeAsync();
            Directory.Delete(Root, true);
        }
        private sealed class Factory(Fixture fixture) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) =>
                fixture.GuardedAddress is not null && name == OutboundConnectGuard.GuardedClientName
                    ? new HttpClient(OutboundConnectGuard.CreateHandler(() => OutboundTargetPolicy.Strict, (_, _) =>
                    {
                        Interlocked.Increment(ref fixture.GuardResolutions);
                        return Task.FromResult(new[] { fixture.GuardedAddress! });
                    }))
                    : new HttpClient(new Handler(fixture));
        }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var payload = await request.Content!.ReadFromJsonAsync<ErrorReportBatch>(cancellationToken);
                fixture.Requests.Enqueue(new Posted(request.RequestUri!.AbsoluteUri, payload!));
                return await fixture.Response(request, cancellationToken);
            }
        }
    }
    private sealed record Posted(string Url, ErrorReportBatch Payload);
    private sealed class SenderScope(AdaPanelDbContext context, ObservedReports reports, ErrorReportSender sender) : IDisposable
    {
        internal ObservedReports Reports { get; } = reports;
        internal ErrorReportSender Sender { get; } = sender;
        public void Dispose() => context.Dispose();
    }
    private sealed class ObservedReports(IErrorReportService inner) : IErrorReportService
    {
        internal TaskCompletionSource SnapshotRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? BeforeMark;
        public Task RecordAsync(Exception exception, string routePattern, string correlationId, CancellationToken cancellationToken = default) => inner.RecordAsync(exception, routePattern, correlationId, cancellationToken);
        public Task<IReadOnlyCollection<ErrorReportDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default) => inner.GetRecentAsync(take, cancellationToken);
        public async Task<IReadOnlyCollection<ErrorReportDto>> GetUnsentAsync(int take, CancellationToken cancellationToken = default)
        {
            var reports = await inner.GetUnsentAsync(take, cancellationToken);
            SnapshotRead.TrySetResult();
            return reports;
        }
        public Task MarkSentAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
        { BeforeMark?.Invoke(); return inner.MarkSentAsync(ids, cancellationToken); }
        public Task MarkSentAsync(IReadOnlyCollection<ErrorReportDto> snapshots, CancellationToken cancellationToken = default)
        { BeforeMark?.Invoke(); return inner.MarkSentAsync(snapshots, cancellationToken); }
        public Task<int> PurgeAsync(int retentionDays = 30, CancellationToken cancellationToken = default) => inner.PurgeAsync(retentionDays, cancellationToken);
    }
    private sealed class Boundary
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task WaitAsync(CancellationToken token) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
    }
    private sealed class SqlBarrier(string kind) : DbCommandInterceptor
    {
        private readonly Boundary boundary = new();
        private int entered;
        internal TaskCompletionSource Entered => boundary.Entered;
        internal TaskCompletionSource Release => boundary.Release;
        private async Task WaitAsync(DbCommand command, CancellationToken token)
        {
            var sql = command.CommandText.TrimStart();
            var matches = sql.Contains("\"error_reports\"", StringComparison.Ordinal) && (kind switch
            {
                "pending" => sql.StartsWith("SELECT", StringComparison.Ordinal) && sql.Contains("IS NULL", StringComparison.Ordinal),
                "mark" => sql.StartsWith("UPDATE", StringComparison.Ordinal) && sql.Contains("\"SentAtUtc\"", StringComparison.Ordinal),
                "record" => sql.StartsWith("UPDATE", StringComparison.Ordinal) && sql.Contains("\"OccurrenceCount\"", StringComparison.Ordinal),
                "purge" => sql.StartsWith("DELETE", StringComparison.Ordinal),
                _ => false
            });
            if (matches && Interlocked.Exchange(ref entered, 1) == 0) await boundary.WaitAsync(token);
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { await WaitAsync(command, cancellationToken); return result; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { await WaitAsync(command, cancellationToken); return result; }
    }
}
