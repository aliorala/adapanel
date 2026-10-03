using System.Text.Json.Nodes;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    [Theory]
    [InlineData("schedule", "context")]
    [InlineData("automation", "context")]
    [InlineData("schedule", "actor")]
    [InlineData("automation", "actor")]
    public async Task ManualPruneRechecksApprovalBetweenRemoteDeletes(string route, string change)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual(route, prune: true);
        var second = await f.AddSecondRemoteArchive();
        var firstRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        f.Cloud.AgHatasi = url =>
        {
            if (f.Cloud.Istekler.Last().Method == "DELETE" && Interlocked.Increment(ref count) == 1)
            {
                firstRequest.TrySetResult(url);
                release.Task.WaitAsync(f.Token).GetAwaiter().GetResult();
            }
            return null;
        };
        // The synchronous fake HTTP hook blocks only this worker, never the test context.
        var action = Task.Run(() => Capture(() => f.InvokeManual(route, f.Admin)), f.Token);
        string firstUrl;
        byte[] state;
        try
        {
            firstUrl = await firstRequest.Task.WaitAsync(f.Token);
            if (change == "actor") await f.Change("disabled", f.Admin);
            else await f.ChangeManualContext("changed");
            state = await f.ManualStateBytes();
        }
        finally { release.TrySetResult(); }
        var error = await action.WaitAsync(f.Token);
        if (change == "actor") AssertAuthorization(error);
        else Assert.Equal("backup.schedule_changed", Assert.IsType<AppException>(error).MessageKey);

        Assert.Equal(state, await f.ManualStateBytes());
        var deleted = firstUrl == f.Record.RemoteUri ? f.Record : second;
        var preserved = ReferenceEquals(deleted, f.Record) ? second : f.Record;
        Assert.False(File.Exists(deleted.Path)); // the already-approved delete completed
        Assert.True(File.Exists(preserved.Path)); // no second mutation after revocation
        Assert.True(File.Exists(f.ManualReplacement.Path));
        Assert.Single(f.Cloud.Istekler, request => request.Method == "DELETE");
        var records = await f.Service.GetBackupsAsync(f.Token);
        Assert.DoesNotContain(records, item => item.Id == deleted.Id);
        Assert.Contains(records, item => item.Id == preserved.Id);
        Assert.Contains(records, item => item.Id == f.ManualReplacement.Id);
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("automation")]
    public async Task ManualRunAllowsTelemetryChangesWithTheSameProgram(string route)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual(route, prune: true);
        await using var held = await f.HoldManual("global");
        var action = Capture(() => f.InvokeManual(route, f.Admin));
        await f.WaitManual(action, "global");
        await f.ChangeManualTelemetry();
        await held.DisposeAsync();
        Assert.Null(await action.WaitAsync(f.Token));
        Assert.Equal(1, f.ManualProduced);
        Assert.False(File.Exists(f.Record.Path));
        Assert.True(File.Exists(f.ManualReplacement.Path));
    }

    [Theory]
    [InlineData("schedule", "failed")]
    [InlineData("automation", "completed_with_errors")]
    public async Task ManualRunPreservesOrdinaryProviderFailureReporting(string route, string expectedStatus)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareManual(route, prune: false);
        await f.InvokeManual(route, f.Admin);
        Assert.Equal(1, f.ProducerBoundaryAttempts);
        if (route == "schedule")
        {
            var result = await f.ManualSchedule.GetScheduleAsync(f.Customer, f.Token);
            Assert.Equal(expectedStatus, result.LastStatus);
            Assert.Contains("fixture.producer_boundary", result.LastDetails);
        }
        else
        {
            var result = await f.ManualAutomation.GetStatusAsync(f.Token);
            Assert.Equal(expectedStatus, result.LastBackupStatus);
            Assert.Contains("fixture.producer_boundary", result.LastBackupDetails);
        }
        Assert.Empty(await f.Service.GetBackupsAsync(f.Token));
        Assert.Empty(f.Cloud.Istekler);
    }

    private sealed partial class Fixture
    {
        private string ManualRelativePath => manualRoute == "schedule"
            ? "config/subscription-backups.json" : "config/automation.json";

        internal Task ChangeManualTelemetry() => Area.DosyaDeposu.UpdateAsync<JsonNode>(ManualRelativePath,
            new JsonObject(), root =>
            {
                var state = manualRoute == "schedule" ? root.AsArray()[0]! : root;
                state[manualRoute == "schedule" ? "lastRunUtc" : "lastBackupRunUtc"] = "2026-01-01T00:00:00Z";
                state[manualRoute == "schedule" ? "lastStatus" : "lastBackupStatus"] = "completed";
                state[manualRoute == "schedule" ? "lastDetails" : "lastBackupDetails"] = "other completed execution";
                return root;
            }, Token);

        internal async Task<BackupRecordDto> AddSecondRemoteArchive()
        {
            var second = await ManualArchive("second-old", 20);
            foreach (var record in new[] { Record, second, ManualReplacement })
            {
                record.TargetType = "webdav";
                record.RemoteUri = "https://backup.example.test/archives/" + record.Name;
                record.RemoteState = BackupPresence.Present;
                record.Status = "created_remote_synced";
            }
            await Area.KayitlariYazAsync(Record, second, ManualReplacement);
            await Area.DosyaDeposu.UpdateAsync<JsonNode>(ManualRelativePath, new JsonObject(), root =>
            {
                var state = manualRoute == "schedule" ? root.AsArray()[0]! : root;
                var prefix = manualRoute == "schedule" ? "" : "backup";
                string Key(string field) => prefix.Length == 0 ? char.ToLowerInvariant(field[0]) + field[1..] : prefix + field;
                state[Key("TargetType")] = "webdav";
                state[Key("RemoteUrl")] = "https://backup.example.test/archives";
                state[Key("RemoteUserName")] = "fixture";
                state[Key("RemotePasswordProtected")] = SahteKoruyucu.Onek + "password";
                return root;
            }, Token);
            return second;
        }
    }
}
