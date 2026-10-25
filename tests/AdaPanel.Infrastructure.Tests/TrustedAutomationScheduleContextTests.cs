using System.Reflection;
using System.Text.Json.Nodes;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.RunningJobs;
using AdaPanel.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

[Collection("Queued backup authority")]
public sealed class TrustedAutomationScheduleContextTests
{
    [Theory]
    [InlineData("disabled")]
    [InlineData("interval")]
    [InlineData("recent_run")]
    public async Task WorkerDoesNotStartSslUsingTheSnapshotFromBeforeItsLongBackup(string change)
    {
        using var f = new Fixture();
        await f.Configure();
        var worker = f.Service.RunDueJobsAsync(f.Token);
        await f.BackupEntered.Task.WaitAsync(f.Token);
        var changedState = await f.Change(change);
        f.BackupRelease.TrySetResult();
        await worker.WaitAsync(f.Token);
        Assert.Equal(0, f.SslCalls);
        Assert.Equal(0, f.MailCalls);
        Assert.Equal(1, f.BackupCalls);
        Assert.Equal(2, f.PruneCalls); // Safe retention runs before production and after publication.
        var current = await f.Read();
        Assert.Equal(changedState["autoSslRenewEnabled"]!.ToJsonString(), current["autoSslRenewEnabled"]!.ToJsonString());
        Assert.Equal(changedState["sslRenewIntervalHours"]!.ToJsonString(), current["sslRenewIntervalHours"]!.ToJsonString());
        foreach (var field in new[] { "lastSslRunUtc", "lastSslStatus", "lastSslDetails" })
            Assert.Equal(changedState[field]!.ToJsonString(), current[field]!.ToJsonString());
        Assert.Equal("completed", current["lastBackupStatus"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerStillRunsDueSslWithoutAnActorOrCookie(bool unrelatedSettingsChanged)
    {
        using var f = new Fixture();
        await f.Configure();
        var worker = f.Service.RunDueJobsAsync(f.Token);
        await f.BackupEntered.Task.WaitAsync(f.Token);
        if (unrelatedSettingsChanged) await f.Change("unrelated");
        f.BackupRelease.TrySetResult();
        await worker.WaitAsync(f.Token);
        Assert.Equal(1, f.SslCalls);
        Assert.Equal(1, f.MailCalls);
        var current = await f.Read();
        Assert.Equal("completed", current["lastSslStatus"]!.GetValue<string>());
        Assert.Equal("completed", current["lastBackupStatus"]!.GetValue<string>());
        Assert.Equal(unrelatedSettingsChanged ? 95 : 90, current["diskUsageThresholdPercent"]!.GetValue<int>());
    }

    [Fact]
    public async Task WorkerCancellationDuringThePriorJobDoesNotStartSslOrPublishATurn()
    {
        using var f = new Fixture();
        await f.Configure();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var before = await f.Bytes();
        var worker = f.Service.RunDueJobsAsync(canceled.Token);
        await f.BackupEntered.Task.WaitAsync(f.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(f.Token));
        Assert.Equal(0, f.SslCalls);
        Assert.Equal(0, f.MailCalls);
        Assert.Equal(before, await f.Bytes());
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly BulutAlani Area = new();
        internal readonly TaskCompletionSource BackupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource BackupRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int BackupCalls, PruneCalls, SslCalls, MailCalls;
        internal readonly AutomationService Service;
        internal CancellationToken Token => Area.Iptal;

        internal Fixture()
        {
            var backup = Stub<IBackupService>((method, args) => method.Name switch
            {
                "BackupPanelAsync" => Produce(args),
                "PruneAsync" => Prune(args),
                _ => throw new NotSupportedException(method.Name)
            });
            var ssl = Stub<ISslService>((method, _) =>
            {
                if (method.Name != "RenewAllAsync") throw new NotSupportedException(method.Name);
                SslCalls++;
                return Task.FromResult(new RenewAllResultDto { Measured = true, BindingsMeasured = true, RenewedCount = 1 });
            });
            var mail = Stub<IMailService>((method, _) =>
            {
                if (method.Name != "SyncCertificateAsync") throw new NotSupportedException(method.Name);
                MailCalls++;
                return Task.FromResult<MailCertificateDto?>(null);
            });
            Service = new AutomationService(Area.DosyaDeposu, backup, ssl, mail,
                Stub<IIisSiteService>(), Stub<IDnsService>(), Stub<IComponentBootstrapper>(), Stub<ISelfUpdateService>(),
                Stub<IDatabaseService>(), Stub<IBruteForceProtectionService>(), new SahteKoruyucu(),
                Stub<INotificationService>(), Stub<ISystemMonitoringService>(), Stub<IHealthService>(),
                Stub<ISchedulerService>(), Stub<IErrorReportSender>(), new ConcurrencyLock(),
                Stub<AdaPanel.Infrastructure.Logs.ILogRotationService>(), Stub<IDatabaseOwnershipService>(),
                new RunningJobService(Area.DosyaDeposu, NullLogger<RunningJobService>.Instance),
                NullLogger<AutomationService>.Instance);
        }

        internal Task Configure() => Area.DosyaDeposu.WriteAsync("config/automation.json", new
        {
            autoBackupEnabled = true, backupSites = false, backupPanelConfig = true,
            autoSslRenewEnabled = true, sslRenewIntervalHours = 1, lastSslRunUtc = DateTime.UtcNow.AddHours(-2),
            lastSslStatus = "previous", lastSslDetails = "previous measured renewal",
            alertsEnabled = false, updateCheckEnabled = false, diskUsageThresholdPercent = 90
        }, Token);

        private async Task<BackupRecordDto> Produce(object?[] args)
        {
            var token = args.OfType<CancellationToken>().Single();
            foreach (var callback in args.OfType<Func<CancellationToken, Task>>()) await callback(token);
            BackupCalls++;
            BackupEntered.TrySetResult();
            await BackupRelease.Task.WaitAsync(token);
            return AutomationBackupOutcomeTests.Result("panel", "panel", []);
        }

        private async Task<BackupPruneResultDto> Prune(object?[] args)
        {
            var token = args.OfType<CancellationToken>().Single();
            foreach (var callback in args.OfType<Func<CancellationToken, Task>>()) await callback(token);
            PruneCalls++;
            return new BackupPruneResultDto();
        }

        internal Task<JsonNode> Change(string change) => Area.DosyaDeposu.UpdateAsync<JsonNode>(
            "config/automation.json", new JsonObject(), current =>
            {
                switch (change)
                {
                    case "disabled": current["autoSslRenewEnabled"] = false; break;
                    case "interval": current["sslRenewIntervalHours"] = 24; break;
                    case "recent_run":
                        current["lastSslRunUtc"] = DateTime.UtcNow;
                        current["lastSslStatus"] = "completed";
                        current["lastSslDetails"] = "newer renewal measurement";
                        break;
                    case "unrelated": current["diskUsageThresholdPercent"] = 95; break;
                    default: throw new InvalidOperationException(change);
                }
                return current;
            }, Token);

        internal Task<JsonNode> Read() => Area.DosyaDeposu.ReadAsync<JsonNode>("config/automation.json", new JsonObject(), Token);
        internal Task<byte[]> Bytes() => File.ReadAllBytesAsync(Area.DosyaDeposu.ResolvePath("config/automation.json"), Token);
        public void Dispose() { BackupRelease.TrySetResult(); Area.Dispose(); }
        private static T Stub<T>(Func<MethodInfo, object?[], object?>? handler = null) where T : class
        {
            var result = DispatchProxy.Create<T, AutomationRunningJobTests.AsyncProxy>();
            if (handler is not null) ((AutomationRunningJobTests.AsyncProxy)(object)result).Handler = (method, args) => handler(method, args ?? []);
            return result;
        }
    }
}
