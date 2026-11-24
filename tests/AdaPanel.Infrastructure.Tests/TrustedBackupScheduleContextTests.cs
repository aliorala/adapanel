using System.Text.Json.Nodes;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Subscriptions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    public static IEnumerable<object[]> WorkerScheduleChanges() =>
        from wait in new[] { "global", "producer", "prune" }
        from change in new[] { "removed", "disabled", "changed" }
        select new object[] { wait, change };

    [Theory]
    [MemberData(nameof(WorkerScheduleChanges))]
    public async Task WorkerScheduleDoesNotApplyOrRepublishAChangedProgram(string wait, string change)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual("schedule", wait == "prune");
        await using var held = await f.HoldManual(wait);
        var action = Capture(() => f.ManualSchedule.RunDueAsync(f.Token));
        await f.WaitManual(action, wait);
        await f.ChangeWorkerSchedule(change);
        var state = await f.ManualStateBytes();
        var index = await f.IndexBytes();
        await held.DisposeAsync();
        Assert.Null(await action.WaitAsync(f.Token)); // worker skips this program and continues its batch
        await f.AssertManualUnchanged(state, index);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("disabled")]
    [InlineData("changed")]
    public async Task WorkerScheduleRechecksProgramBetweenRemoteDeletes(string change)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual("schedule", prune: true);
        var second = await f.AddSecondRemoteArchive();
        var firstRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        f.Cloud.AgHatasi = url =>
        {
            if (f.Cloud.Istekler.Last().Method == "DELETE" && Interlocked.Increment(ref requests) == 1)
            {
                firstRequest.TrySetResult(url);
                release.Task.WaitAsync(f.Token).GetAwaiter().GetResult();
            }
            return null;
        };
        var action = Task.Run(() => Capture(() => f.ManualSchedule.RunDueAsync(f.Token)), f.Token);
        string firstUrl;
        byte[] state;
        try
        {
            firstUrl = await firstRequest.Task.WaitAsync(f.Token);
            await f.ChangeWorkerSchedule(change);
            state = await f.ManualStateBytes();
        }
        finally { release.TrySetResult(); }
        Assert.Null(await action.WaitAsync(f.Token));
        var deleted = firstUrl == f.Record.RemoteUri ? f.Record : second;
        var preserved = ReferenceEquals(deleted, f.Record) ? second : f.Record;
        Assert.False(File.Exists(deleted.Path));
        Assert.True(File.Exists(preserved.Path));
        Assert.True(File.Exists(f.ManualReplacement.Path));
        Assert.Single(f.Cloud.Istekler, request => request.Method == "DELETE");
        Assert.Equal(state, await f.ManualStateBytes());
        var records = await f.Service.GetBackupsAsync(f.Token);
        Assert.DoesNotContain(records, item => item.Id == deleted.Id);
        Assert.Contains(records, item => item.Id == preserved.Id);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("producer")]
    [InlineData("prune")]
    public async Task WorkerScheduleCancellationPropagatesWithoutPublication(string wait)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual("schedule", wait == "prune");
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        await using var held = await f.HoldManual(wait);
        var action = Capture(() => f.ManualSchedule.RunDueAsync(canceled.Token));
        await f.WaitManual(action, wait);
        var state = await f.ManualStateBytes();
        var index = await f.IndexBytes();
        canceled.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        await held.DisposeAsync();
        await f.AssertManualUnchanged(state, index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerScheduleStillRunsWithoutAnActorAndAllowsTelemetryChanges(bool telemetryChanged)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual("schedule", prune: true);
        // The worker is configured authority. An unrelated admin session is not its actor.
        await f.Change("disabled", f.Admin);
        await using var held = await f.HoldManual("global");
        var action = Capture(() => f.ManualSchedule.RunDueAsync(f.Token));
        await f.WaitManual(action, "global");
        if (telemetryChanged) await f.ChangeManualTelemetry();
        await held.DisposeAsync();
        Assert.Null(await action.WaitAsync(f.Token));
        Assert.Equal(1, f.ManualProduced);
        Assert.False(File.Exists(f.Record.Path));
        Assert.True(File.Exists(f.ManualReplacement.Path));
        Assert.Equal("completed", (await f.ManualSchedule.GetScheduleAsync(f.Customer, f.Token)).LastStatus);
    }

    [Fact]
    public async Task WorkerScheduleSkipsRemovedProgramButStillRunsItsNeighbor()
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual("schedule", prune: true);
        var produced = new List<Guid>();
        var pruned = new List<Guid>();
        await f.PrepareWorkerNeighbor(produced, pruned);
        await using var held = await f.HoldManual("global");
        var action = Capture(() => f.ManualSchedule.RunDueAsync(f.Token));
        await f.WaitManual(action, "global");
        await f.ChangeWorkerSchedule("removed");
        await held.DisposeAsync();
        Assert.Null(await action.WaitAsync(f.Token));
        Assert.Equal(new[] { f.Other }, produced);
        Assert.Equal(new[] { f.Other, f.Other }, pruned); // Both retention phases stay scoped to the remaining neighbor.
        var remaining = Assert.Single(await f.ManualSchedule.GetSchedulesAsync(f.Token));
        Assert.Equal(f.Other, remaining.UserId);
        Assert.Equal("completed", remaining.LastStatus);
    }

    private sealed partial class Fixture
    {
        internal Task ChangeWorkerSchedule(string change) => change switch
        {
            "removed" => ManualSchedule.RemoveScheduleAsync(Customer, Token),
            "disabled" => Area.DosyaDeposu.UpdateAsync<JsonNode>(ManualRelativePath, new JsonArray(), root =>
            { root.AsArray()[0]!["enabled"] = false; return root; }, Token),
            _ => ChangeManualContext("changed")
        };

        internal async Task PrepareWorkerNeighbor(List<Guid> produced, List<Guid> pruned)
        {
            await Area.DosyaDeposu.UpdateAsync<JsonNode>(ManualRelativePath, new JsonArray(), root =>
            {
                var neighbor = root.AsArray()[0]!.DeepClone();
                neighbor["userId"] = Other.ToString("N");
                root.AsArray().Add(neighbor);
                return root;
            }, Token);
            async Task<BackupRecordDto> Produce(object?[] args)
            {
                foreach (var callback in args.OfType<Func<CancellationToken, Task>>()) await callback(Token);
                var id = args.OfType<BackupSubscriptionRequest>().Single().UserId;
                produced.Add(id);
                return new BackupRecordDto { Id = id.ToString("N"), Type = "subscription", Source = id.ToString("N"),
                    Name = "measured.zip", Status = "created", ItemCount = 1, Size = 1024 };
            }
            async Task<BackupPruneResultDto> Prune(object?[] args)
            {
                foreach (var callback in args.OfType<Func<CancellationToken, Task>>()) await callback(Token);
                pruned.Add(args.OfType<Guid>().Single());
                return new BackupPruneResultDto();
            }
            var backups = Fake<IBackupService>((method, args) => method.Name switch
            {
                "BackupSubscriptionAsync" => Produce(args),
                "PruneSubscriptionAsync" => Prune(args),
                _ => throw new NotSupportedException(method.Name)
            });
            ManualSchedule = new SubscriptionBackupScheduleService(Area.DosyaDeposu, backups, Scopes,
                Fake<IServicePlanService>((method, _) => throw new NotSupportedException(method.Name)), new SahteKoruyucu(),
                manualGate, Jobs, NullLogger<SubscriptionBackupScheduleService>.Instance);
        }
    }
}
