using System.Reflection;
using System.Text.Json.Nodes;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.RunningJobs;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Subscriptions;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Actual schedule/scope/plan JSON services and running-job bookkeeping. Only archive production
/// is fake: its barrier is immediately before the real producer's beforeBackup callback boundary.
/// No Windows service, database dump, socket or remote backup is used.
/// </summary>
public sealed class SubscriptionBackupDueLifecycleTests
{
    [Theory]
    [InlineData("global", false)]
    [InlineData("global", true)]
    [InlineData("producer", false)]
    [InlineData("producer", true)]
    public async Task AQueuedScheduledRunRechecksTheCurrentPlanBeforeProducing(string wait, bool assignNewPlan)
    {
        using var f = new Fixture();
        await f.Initialize();
        var previous = await f.Schedules.GetScheduleAsync(f.User, f.Token);
        await using var held = wait == "global" ? await f.Gate.Hold(f.Token) : null;
        var entered = wait == "global" ? f.Gate.ObserveNextEntry() : f.PauseProducer();
        var run = f.Schedules.RunDueAsync(f.Token);
        try
        {
            await entered.WaitAsync(f.Token);
            await f.TightenPlan(assignNewPlan);
        }
        finally
        {
            if (held is not null) await held.DisposeAsync();
            f.ReleaseProducer();
        }
        await run.WaitAsync(f.Token);

        Assert.Empty(f.Produced);
        Assert.Equal(wait == "producer" ? 1 : 0, f.Pruned.Count);
        Assert.All(f.Pruned, id => Assert.Equal(f.User, id));
        var current = await f.Schedules.GetScheduleAsync(f.User, f.Token);
        Assert.Equal(previous.LastRunUtc, current.LastRunUtc);
        Assert.Equal(previous.LastStatus, current.LastStatus);
        Assert.Equal(previous.LastDetails, current.LastDetails);
        Assert.Equal(1, current.MaxBackupsPerDay);
        Assert.Equal(24, current.IntervalHours);
        Assert.Equal(previous.LastRunUtc!.Value.AddHours(24), current.NextRunUtc);
        Assert.Empty((await f.Jobs.GetStatusAsync([RunningJobKinds.Backup], null, f.Token)).Running);
    }

    [Theory]
    [InlineData(false, "interval")]
    [InlineData(true, "interval")]
    [InlineData(false, "daily")]
    [InlineData(true, "daily")]
    public async Task AManualCompletionWhileTheWorkerResolvesThePlanDoesNotRunAgain(bool manualFails, string mode)
    {
        using var f = new Fixture();
        await f.Initialize();
        if (mode == "daily")
        {
            var request = Fixture.Request();
            request.ScheduleMode = mode;
            request.ScheduleHour = DateTime.Now.Hour;
            request.ScheduleMinute = DateTime.Now.Minute;
            await f.Schedules.UpdateScheduleAsync(f.User, request, f.Token);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeNextScopeRead = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var worker = f.Schedules.RunDueAsync(f.Token);
        SubscriptionBackupScheduleDto manual;
        try
        {
            await entered.Task.WaitAsync(f.Token);
            f.FailNextProduction = manualFails;
            // Same running-job admission used by BackupController.RunScheduleNowAsync. The
            // worker has read the old program but has not acquired this user's job yet.
            await using (await f.Jobs.BeginAsync(RunningJobKinds.Backup, f.User.ToString("N"), f.User, f.Token))
                manual = await f.Schedules.RunNowAsync(f.User, _ => Task.CompletedTask, f.Token);
        }
        finally { release.TrySetResult(); }
        await worker.WaitAsync(f.Token);

        Assert.Single(f.Produced);
        Assert.Equal(manualFails ? "failed" : "completed", manual.LastStatus);
        var current = await f.Schedules.GetScheduleAsync(f.User, f.Token);
        Assert.Equal(manual.LastRunUtc, current.LastRunUtc);
        Assert.Equal(manual.LastStatus, current.LastStatus);
        Assert.Equal(manual.LastDetails, current.LastDetails);
        Assert.Equal(manual.NextRunUtc, current.NextRunUtc);
        Assert.True(current.NextRunUtc > current.LastRunUtc);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("producer")]
    public async Task AStillDueRunRemainsAllowedAfterThePlanChanges(string wait)
    {
        using var f = new Fixture();
        await f.Initialize();
        await f.SetLastRun(f.User, DateTime.UtcNow.AddHours(-30));
        await using var held = wait == "global" ? await f.Gate.Hold(f.Token) : null;
        var entered = wait == "global" ? f.Gate.ObserveNextEntry() : f.PauseProducer();
        var run = f.Schedules.RunDueAsync(f.Token);
        try { await entered.WaitAsync(f.Token); await f.TightenPlan(false); }
        finally { if (held is not null) await held.DisposeAsync(); f.ReleaseProducer(); }
        await run.WaitAsync(f.Token);
        Assert.Equal(f.User, Assert.Single(f.Produced));
        Assert.Equal(2, f.Pruned.Count);
        Assert.All(f.Pruned, id => Assert.Equal(f.User, id));
        Assert.Equal("completed", (await f.Schedules.GetScheduleAsync(f.User, f.Token)).LastStatus);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("producer")]
    public async Task CancellationWhileWaitingDoesNotProduceOrWriteACompletedRun(string wait)
    {
        using var f = new Fixture();
        await f.Initialize();
        var previous = await f.Schedules.GetScheduleAsync(f.User, f.Token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        await using var held = wait == "global" ? await f.Gate.Hold(f.Token) : null;
        var entered = wait == "global" ? f.Gate.ObserveNextEntry() : f.PauseProducer();
        var run = f.Schedules.RunDueAsync(canceled.Token);
        try { await entered.WaitAsync(f.Token); canceled.Cancel(); }
        finally { if (held is not null) await held.DisposeAsync(); f.ReleaseProducer(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(f.Produced);
        Assert.Equal(wait == "producer" ? 1 : 0, f.Pruned.Count);
        Assert.All(f.Pruned, id => Assert.Equal(f.User, id));
        Assert.Equal(previous.LastRunUtc, (await f.Schedules.GetScheduleAsync(f.User, f.Token)).LastRunUtc);
        Assert.Empty((await f.Jobs.GetStatusAsync([RunningJobKinds.Backup], null, f.Token)).Running);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ManualRunsStillBypassTheDailyPlanLimit(int maxPerDay)
    {
        using var f = new Fixture();
        await f.Initialize();
        await f.Plans.UpdatePlanAsync(f.PlanId, new ServicePlanRequest { Name = "current", MaxBackupsPerDay = maxPerDay }, f.Token);
        await f.SetLastRun(f.User, DateTime.UtcNow);
        var result = await f.Schedules.RunNowAsync(f.User, _ => Task.CompletedTask, f.Token);
        Assert.Equal(f.User, Assert.Single(f.Produced));
        Assert.Equal("completed", result.LastStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APlanChangeAfterProductionDoesNotDiscardTheCompletedArchiveOutcome(bool assignNewPlan)
    {
        using var f = new Fixture();
        await f.Initialize();
        f.AfterProduction = _ => f.TightenPlan(assignNewPlan);
        await f.Schedules.RunDueAsync(f.Token);
        Assert.Equal(f.User, Assert.Single(f.Produced));
        Assert.Equal(2, f.Pruned.Count);
        Assert.All(f.Pruned, id => Assert.Equal(f.User, id));
        var current = await f.Schedules.GetScheduleAsync(f.User, f.Token);
        Assert.Equal("completed", current.LastStatus);
        Assert.Contains("measured.zip", current.LastDetails);
        Assert.Equal(current.LastRunUtc!.Value.AddHours(24), current.NextRunUtc);
    }

    [Fact]
    public async Task SkippingAQueuedRunDoesNotPreventTheNextDueSubscription()
    {
        using var f = new Fixture();
        await f.Initialize();
        var neighbor = Guid.NewGuid();
        await f.Schedules.UpdateScheduleAsync(neighbor, Fixture.Request(), f.Token);
        await using var held = await f.Gate.Hold(f.Token);
        var entered = f.Gate.ObserveNextEntry();
        var run = f.Schedules.RunDueAsync(f.Token);
        try { await entered.WaitAsync(f.Token); await f.TightenPlan(false); }
        finally { await held.DisposeAsync(); }
        await run.WaitAsync(f.Token);
        Assert.Equal(neighbor, Assert.Single(f.Produced));
        Assert.Equal(2, f.Pruned.Count);
        Assert.All(f.Pruned, id => Assert.Equal(neighbor, id));
        Assert.Equal("completed", (await f.Schedules.GetScheduleAsync(neighbor, f.Token)).LastStatus);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-due-lifecycle-" + Guid.NewGuid().ToString("N"));
        private TaskCompletionSource? producerEntered;
        private TaskCompletionSource? producerRelease;
        internal readonly Guid User = Guid.NewGuid();
        internal string PlanId = "";
        internal readonly List<Guid> Produced = [];
        internal readonly List<Guid> Pruned = [];
        internal readonly ObservedGate Gate = new();
        internal readonly JsonFileStore Store;
        internal readonly RunningJobService Jobs;
        internal readonly ServicePlanService Plans;
        internal readonly CustomerScopeService Scopes;
        internal readonly SubscriptionBackupScheduleService Schedules;
        internal Func<CancellationToken, Task>? BeforeNextScopeRead;
        internal Func<CancellationToken, Task>? AfterProduction;
        internal bool FailNextProduction;
        internal CancellationToken Token => lifetime.Token;

        internal Fixture()
        {
            Directory.CreateDirectory(root);
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Jobs = new RunningJobService(Store, NullLogger<RunningJobService>.Instance);
            Plans = new ServicePlanService(Store);
            Scopes = new CustomerScopeService(Store, Plans,
                Fake<IResellerQuotaService>((method, _) => method.Name == "GetQuotaAsync"
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)),
                Fake<IIisSiteService>((method, _) => throw new NotSupportedException(method.Name)));
            var observedScope = Fake<ICustomerScopeService>((method, args) => method.Name == "GetScopeAsync"
                ? GetScope((Guid)args[0]!, (CancellationToken)args[1]!) : throw new NotSupportedException(method.Name));
            var backups = Fake<IBackupService>((method, args) => method.Name switch
            {
                "BackupSubscriptionAsync" => Produce(args),
                "PruneSubscriptionAsync" => Prune(args),
                _ => throw new NotSupportedException(method.Name)
            });
            Schedules = new SubscriptionBackupScheduleService(Store, backups, observedScope, Plans,
                new IdentitySecrets(), Gate, Jobs, NullLogger<SubscriptionBackupScheduleService>.Instance);
        }

        internal async Task Initialize()
        {
            PlanId = (await Plans.CreatePlanAsync(new ServicePlanRequest { Name = "initial", MaxBackupsPerDay = 24 }, cancellationToken: Token)).Id;
            await Scopes.SetPlanAsync(User, PlanId, Token);
            await Schedules.UpdateScheduleAsync(User, Request(), Token);
            await SetLastRun(User, DateTime.UtcNow.AddHours(-2));
        }

        internal static UpdateSubscriptionBackupScheduleRequest Request() => new()
        { Enabled = true, ScheduleMode = "interval", IntervalHours = 1, TargetType = "local" };

        private async Task<CustomerScope> GetScope(Guid id, CancellationToken token)
        {
            var before = Interlocked.Exchange(ref BeforeNextScopeRead, null);
            if (before is not null) await before(token);
            return await Scopes.GetScopeAsync(id, token);
        }

        internal async Task TightenPlan(bool assignNewPlan)
        {
            var request = new ServicePlanRequest { Name = "tightened", MaxBackupsPerDay = 1 };
            if (assignNewPlan)
                await Scopes.SetPlanAsync(User, (await Plans.CreatePlanAsync(request, cancellationToken: Token)).Id, Token);
            else await Plans.UpdatePlanAsync(PlanId, request, Token);
        }

        internal Task SetLastRun(Guid id, DateTime value) => Store.UpdateAsync<JsonNode>("config/subscription-backups.json", new JsonArray(), state =>
        {
            var item = state.AsArray().Single(row => row!["userId"]!.GetValue<string>() == id.ToString("N"))!;
            item["lastRunUtc"] = value;
            item["lastStatus"] = "completed";
            item["lastDetails"] = "existing completed run";
            return state;
        }, Token);

        internal Task PauseProducer()
        {
            producerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            producerRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return producerEntered.Task;
        }
        internal void ReleaseProducer() => producerRelease?.TrySetResult();

        private async Task<BackupRecordDto> Produce(object?[] args)
        {
            var token = args.OfType<CancellationToken>().Single();
            var entered = Interlocked.Exchange(ref producerEntered, null);
            if (entered is not null)
            {
                entered.TrySetResult();
                await producerRelease!.Task.WaitAsync(token);
            }
            foreach (var callback in args.OfType<Func<CancellationToken, Task>>()) await callback(token);
            token.ThrowIfCancellationRequested();
            var id = args.OfType<BackupSubscriptionRequest>().Single().UserId;
            Produced.Add(id);
            if (FailNextProduction) { FailNextProduction = false; throw new IOException("fixture archive failed"); }
            if (AfterProduction is not null) await AfterProduction(token);
            return new BackupRecordDto { Id = Guid.NewGuid().ToString("N"), Name = "measured.zip", Type = "subscription",
                Source = id.ToString("N"), Status = "created", ItemCount = 1, Size = 1024 };
        }

        private async Task<BackupPruneResultDto> Prune(object?[] args)
        {
            var token = args.OfType<CancellationToken>().Single();
            foreach (var callback in args.OfType<Func<CancellationToken, Task>>()) await callback(token);
            Pruned.Add(args.OfType<Guid>().Single());
            return new BackupPruneResultDto();
        }

        public void Dispose()
        {
            lifetime.Cancel();
            ReleaseProducer();
            lifetime.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private sealed class IdentitySecrets : ISecretProtector
    { public string Protect(string value) => value; public string Unprotect(string value) => value; }

    private sealed class ObservedGate : IConcurrencyLock
    {
        private readonly ConcurrencyLock inner = new();
        private readonly string prefix = "due-fixture-" + Guid.NewGuid().ToString("N") + ":";
        private TaskCompletionSource? nextEntry;
        internal Task ObserveNextEntry() => (nextEntry = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public Task RunExclusiveAsync(Func<Task> work, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, work, token);
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> work, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, work, token);
        public Task RunExclusiveAsync(string key, Func<Task> work, CancellationToken token = default)
        { Interlocked.Exchange(ref nextEntry, null)?.TrySetResult(); return inner.RunExclusiveAsync(prefix + key, work, token); }
        public Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> work, CancellationToken token = default)
        { Interlocked.Exchange(ref nextEntry, null)?.TrySetResult(); return inner.RunExclusiveAsync(prefix + key, work, token); }
        internal async Task<HeldGate> Hold(CancellationToken token)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = inner.RunExclusiveAsync(prefix + ConcurrencyKeys.SubscriptionBackup,
                async () => { entered.TrySetResult(); await release.Task.WaitAsync(token); }, token);
            await entered.Task.WaitAsync(token);
            return new HeldGate(release, work);
        }
    }

    private sealed class HeldGate(TaskCompletionSource release, Task work) : IAsyncDisposable
    { public async ValueTask DisposeAsync() { release.TrySetResult(); await work; } }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> callback) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Callback = callback; return result; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Callback = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Callback(method!, args ?? []);
    }
}
