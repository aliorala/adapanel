using System.IO.Compression;
using System.Reflection;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    public static IEnumerable<object[]> ManualAdminRevocations() =>
        from route in new[] { "schedule", "automation" }
        from wait in new[] { "global", "producer", "prune" }
        from change in new[] { "disabled", "deleted", "role", "password" }
        select new object[] { route, wait, change };

    [Theory]
    [MemberData(nameof(ManualAdminRevocations))]
    public async Task ManualRunDoesNotLoseActorAuthorityInsideSchedulerOrAutomation(string route, string wait, string change)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual(route, wait == "prune");
        await using var held = await f.HoldManual(wait);
        var action = Capture(() => f.InvokeManual(route, f.Admin));
        await f.WaitManual(action, wait);
        await f.Change(change, f.Admin);
        var state = await f.ManualStateBytes();
        var index = await f.IndexBytes();
        await held.DisposeAsync();
        AssertAuthorization(await action.WaitAsync(f.Token));
        await f.AssertManualUnchanged(state, index);
    }

    public static IEnumerable<object[]> ManualTenantRevocations() =>
        from wait in new[] { "global", "producer", "prune" }
        from change in new[] { "section", "parent_disabled", "parent_deleted", "reparented", "detached" }
        select new object[] { wait, change };

    [Theory]
    [MemberData(nameof(ManualTenantRevocations))]
    public async Task ManualSubscriptionRunRechecksParentAndBackupSection(string wait, string change)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual("schedule", wait == "prune");
        await using var held = await f.HoldManual(wait);
        var action = Capture(() => f.InvokeManual("schedule", f.Child));
        await f.WaitManual(action, wait);
        await f.Change(change, f.Child);
        var state = await f.ManualStateBytes();
        var index = await f.IndexBytes();
        await held.DisposeAsync();
        AssertAuthorization(await action.WaitAsync(f.Token));
        await f.AssertManualUnchanged(state, index);
    }

    [Theory]
    [InlineData("schedule", "global", "removed")]
    [InlineData("schedule", "producer", "removed")]
    [InlineData("schedule", "prune", "removed")]
    [InlineData("schedule", "global", "changed")]
    [InlineData("schedule", "producer", "changed")]
    [InlineData("schedule", "prune", "changed")]
    [InlineData("automation", "global", "changed")]
    [InlineData("automation", "producer", "changed")]
    [InlineData("automation", "prune", "changed")]
    public async Task ManualRunCannotApplyAProgramContextThatChangedWhileQueued(string route, string wait, string change)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual(route, wait == "prune");
        await using var held = await f.HoldManual(wait);
        var action = Capture(() => f.InvokeManual(route, f.Admin));
        await f.WaitManual(action, wait);
        await f.ChangeManualContext(change);
        var state = await f.ManualStateBytes();
        var index = await f.IndexBytes();
        await held.DisposeAsync();
        var error = Assert.IsType<AppException>(await action.WaitAsync(f.Token));
        Assert.Equal("backup.schedule_changed", error.MessageKey);
        Assert.Equal(409, error.HttpStatusCode);
        await f.AssertManualUnchanged(state, index);
    }

    [Theory]
    [InlineData("schedule", "global")]
    [InlineData("schedule", "producer")]
    [InlineData("schedule", "prune")]
    [InlineData("automation", "global")]
    [InlineData("automation", "producer")]
    [InlineData("automation", "prune")]
    public async Task ManualRunCancellationBeforeMutationPropagatesWithoutPublishingFailure(string route, string wait)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual(route, wait == "prune");
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        await using var held = await f.HoldManual(wait);
        var action = Capture(() => f.InvokeManual(route, f.Admin, canceled.Token));
        await f.WaitManual(action, wait);
        var state = await f.ManualStateBytes();
        var index = await f.IndexBytes();
        canceled.Cancel();
        // Keep the lease held until cancellation has been delivered to the waiter.
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        await held.DisposeAsync();
        await f.AssertManualUnchanged(state, index);
    }

    [Theory]
    [InlineData("schedule", false)]
    [InlineData("automation", false)]
    [InlineData("schedule", true)]
    [InlineData("automation", true)]
    public async Task ManualAndTrustedCallsKeepTheExistingMeasuredPruneContract(string route, bool trusted)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual(route, prune: true);
        if (trusted)
        {
            if (route == "schedule") await f.ManualSchedule.RunNowAsync(f.Customer, f.Token);
            else await f.ManualAutomation.RunBackupNowAsync(f.Token);
        }
        else await f.InvokeManual(route, route == "schedule" ? f.Child : f.Admin);
        Assert.Equal(1, f.ManualProduced);
        Assert.False(File.Exists(f.Record.Path));
        Assert.True(File.Exists(f.ManualReplacement.Path));
        Assert.Equal(f.ManualReplacement.Id, Assert.Single(await f.Service.GetBackupsAsync(f.Token)).Id);
        if (route == "schedule") Assert.Equal("completed", (await f.ManualSchedule.GetScheduleAsync(f.Customer, f.Token)).LastStatus);
        else Assert.Equal("completed", (await f.ManualAutomation.GetStatusAsync(f.Token)).LastBackupStatus);
    }

    private sealed partial class Fixture
    {
        internal SubscriptionBackupScheduleService ManualSchedule = null!;
        internal AutomationService ManualAutomation = null!;
        internal BackupRecordDto ManualReplacement = null!;
        internal int ManualProduced;
        private ObservedManualGate manualGate = null!;
        private string manualRoute = "";
        private string ManualStatePath => Area.DosyaDeposu.ResolvePath(manualRoute == "schedule"
            ? "config/subscription-backups.json" : "config/automation.json");

        internal async Task PrepareManual(string route, bool prune)
        {
            manualRoute = route;
            await PrepareProducer(route == "schedule" ? "subscription" : "panel");
            manualGate = new ObservedManualGate();
            IBackupService backup = Service;
            if (prune)
            {
                Record = await ManualArchive("old", 30);
                ManualReplacement = await ManualArchive("replacement", 0);
                await Area.KayitlariYazAsync(Record, ManualReplacement);
                backup = Fake<IBackupService>((method, args) =>
                {
                    if (method.Name is "BackupSubscriptionAsync" or "BackupPanelAsync") return ManualProduce(args);
                    // Real source lease, planner, ZIP deletion and JSON publication are exercised.
                    try { return method.Invoke(Service, args); }
                    catch (TargetInvocationException error) when (error.InnerException is not null)
                    { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
                });
            }
            ManualSchedule = new SubscriptionBackupScheduleService(Area.DosyaDeposu, backup, Scopes,
                Fake<IServicePlanService>((method, _) => throw new NotSupportedException(method.Name)), new SahteKoruyucu(),
                manualGate, Jobs, NullLogger<SubscriptionBackupScheduleService>.Instance);
            ManualAutomation = new AutomationService(Area.DosyaDeposu, backup,
                null!, null!, null!, null!, null!, null!, null!, null!, new SahteKoruyucu(),
                null!, null!, null!, null!, null!, manualGate, null!, null!, Jobs, NullLogger<AutomationService>.Instance);
            await WriteManualContext(retention: 1);
        }

        private async Task<BackupRecordDto> ManualArchive(string name, int days)
        {
            var path = Path.Combine(Area.DosyaDeposu.EnsureDirectory("manual-archives"), name + ".zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("payload.txt").Open())) writer.Write(name);
            return new BackupRecordDto
            {
                Id = Guid.NewGuid().ToString("N"), Name = name + ".zip", Path = path, Format = "zip",
                Type = Record.Type, Source = Record.Source, SubscriptionUserId = manualRoute == "schedule" ? Customer.ToString("N") : "",
                Status = "created", TargetType = "local", CreatedAtUtc = DateTime.UtcNow.AddDays(-days),
                Size = new FileInfo(path).Length, ContentHash = await BackupContentHash.ComputeAsync(path, Token),
                IntegrityState = BackupIntegrity.Sound, IncludedDatabases = [], IncludedComponents = [], ItemCount = 1
            };
        }

        private async Task<BackupRecordDto> ManualProduce(object?[] args)
        {
            var ct = args.OfType<CancellationToken>().Single();
            foreach (var authorize in args.OfType<Func<CancellationToken, Task>>()) await authorize(ct);
            ManualProduced++;
            return ManualReplacement;
        }

        private Task WriteManualContext(int retention) => manualRoute == "schedule"
            ? Area.DosyaDeposu.WriteAsync("config/subscription-backups.json", new[] { new
            {
                userId = Customer.ToString("N"), enabled = true, retentionDays = retention, keepCount = 0,
                includeFiles = true, includeDatabases = false, includeMail = false, includeFtp = false,
                targetType = "local", remoteUrl = "", lastStatus = "never"
            } }, Token)
            : Area.DosyaDeposu.WriteAsync("config/automation.json", new
            {
                autoBackupEnabled = true, backupSites = false, backupDatabases = false, backupMail = false,
                backupPanelConfig = true, backupRetentionDays = retention, backupTargetType = "local",
                autoSslRenewEnabled = false, updateCheckEnabled = false, lastBackupStatus = "never"
            }, Token);

        internal Task ChangeManualContext(string change) => change == "removed"
            ? Area.DosyaDeposu.WriteAsync("config/subscription-backups.json", Array.Empty<object>(), Token)
            : WriteManualContext(retention: 7);

        internal async Task InvokeManual(string route, Guid actor, CancellationToken? token = null)
        {
            var controller = Controller(actor, ManualSchedule);
            if (route == "schedule") await controller.RunScheduleNowAsync(Customer, token ?? Token);
            else
            {
                var automation = ActivatorUtilities.CreateInstance<AutomationController>(
                    controller.HttpContext.RequestServices, ManualAutomation, Area.Denetim);
                automation.ControllerContext = controller.ControllerContext;
                await automation.RunBackupAsync(token ?? Token);
            }
        }

        internal async Task<ManualHold> HoldManual(string wait)
        {
            if (wait != "global") return new ManualHold(await EnterSource());
            return await manualGate.HoldAsync(manualRoute == "schedule" ? ConcurrencyKeys.SubscriptionBackup : ConcurrencyKeys.Automation, Token);
        }
        internal async Task WaitManual(Task action, string wait)
        {
            if (wait == "global") await manualGate.Attempted.Task.WaitAsync(Token);
            else await WaitForProducerSource(action);
            Assert.False(action.IsCompleted);
        }
        internal Task<byte[]> ManualStateBytes() => File.ReadAllBytesAsync(ManualStatePath, Token);
        internal async Task AssertManualUnchanged(byte[] state, byte[] index)
        {
            Assert.Equal(0, ProducerBoundaryAttempts);
            Assert.Equal(state, await ManualStateBytes());
            Assert.Equal(index, await IndexBytes());
            Assert.Empty(Cloud.Istekler);
            Assert.Equal(0, OutboundAttempts);
            if (ManualReplacement is not null)
            {
                Assert.True(File.Exists(Record.Path));
                Assert.True(File.Exists(ManualReplacement.Path));
            }
            else Assert.False(Directory.Exists(Path.Combine(Area.Kok, "backups")));
        }
    }

    private sealed class ManualHold : IAsyncDisposable
    {
        private IDisposable? source;
        private TaskCompletionSource? release;
        private readonly Task? task;
        internal ManualHold(IDisposable source) { this.source = source; }
        internal ManualHold(TaskCompletionSource release, Task task) { this.release = release; this.task = task; }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref source, null)?.Dispose();
            Interlocked.Exchange(ref release, null)?.TrySetResult();
            if (task is not null) await task;
        }
    }

    private sealed class ObservedManualGate : IConcurrencyLock
    {
        private readonly ConcurrencyLock actual = new();
        internal readonly TaskCompletionSource Attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken = default) =>
            RunExclusiveAsync(ConcurrencyKeys.Iis, operation, cancellationToken);
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) =>
            RunExclusiveAsync(ConcurrencyKeys.Iis, operation, cancellationToken);
        public Task RunExclusiveAsync(string key, Func<Task> operation, CancellationToken cancellationToken = default)
        { Attempted.TrySetResult(); return actual.RunExclusiveAsync(key, operation, cancellationToken); }
        public Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> operation, CancellationToken cancellationToken = default)
        { Attempted.TrySetResult(); return actual.RunExclusiveAsync(key, operation, cancellationToken); }
        internal async Task<ManualHold> HoldAsync(string key, CancellationToken token)
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = actual.RunExclusiveAsync(key, async () => { held.TrySetResult(); await release.Task.WaitAsync(token); }, token);
            await held.Task.WaitAsync(token);
            return new ManualHold(release, task);
        }
    }
}
