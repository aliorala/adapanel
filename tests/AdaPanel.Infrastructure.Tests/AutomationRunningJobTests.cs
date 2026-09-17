using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.RunningJobs;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AutomationRunningJobTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Manual_and_scheduled_jobs_are_visible_until_pruning_finishes(bool scheduled, bool fail)
    {
        using var fixture = new Fixture();
        await fixture.Configure();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<BackupPruneResultDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backup.Handler = (method, _) => method.Name switch
        {
            nameof(IBackupService.BackupPanelAsync) => Task.FromResult(new BackupRecordDto()),
            nameof(IBackupService.PruneAsync) => Wait(),
            _ => throw new NotSupportedException(method.Name)
        };
        Task<BackupPruneResultDto> Wait() { entered.TrySetResult(); return finish.Task; }
        var task = scheduled ? fixture.Service.RunDueJobsAsync() : fixture.Service.RunBackupNowAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var running = Assert.Single((await fixture.Jobs.GetStatusAsync([RunningJobKinds.Backup], null)).Running);
            Assert.Equal("automation", running.Title);
            Assert.Empty((await fixture.Jobs.GetStatusAsync([RunningJobKinds.Backup], new HashSet<string>())).Running);
            var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.RunBackupNowAsync());
            Assert.Equal(409, error.HttpStatusCode);
        }
        finally
        {
            if (fail) finish.TrySetException(new IOException("test prune failure"));
            else finish.TrySetResult(new BackupPruneResultDto());
            await task;
        }
        Assert.Empty((await fixture.Jobs.GetStatusAsync([RunningJobKinds.Backup], null)).Running);
        Assert.Equal(fail ? "failed" : "completed", (await fixture.Service.GetStatusAsync()).LastBackupStatus);
    }

    [Fact]
    public async Task Busy_scheduled_backup_is_deferred_without_consuming_its_schedule()
    {
        using var fixture = new Fixture();
        await fixture.Configure();
        await using var existing = await fixture.Jobs.BeginAsync(RunningJobKinds.Backup, "panel", null);
        await fixture.Service.RunDueJobsAsync();
        Assert.Null((await fixture.Service.GetStatusAsync()).LastBackupRunUtc);
        Assert.Equal("panel", Assert.Single((await fixture.Jobs.GetStatusAsync([RunningJobKinds.Backup], null)).Running).Title);
    }

    [Fact]
    public async Task Cancellation_clears_the_running_record()
    {
        using var fixture = new Fixture();
        await fixture.Configure();
        fixture.Backup.Handler = (method, _) => method.Name == nameof(IBackupService.PruneAsync)
            ? Task.FromCanceled<BackupPruneResultDto>(new CancellationToken(true))
            : Task.FromResult(new BackupRecordDto());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RunBackupNowAsync());
        Assert.Empty((await fixture.Jobs.GetStatusAsync([RunningJobKinds.Backup], null)).Running);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-automation-jobs-" + Guid.NewGuid().ToString("N"));
        private readonly JsonFileStore store;
        public readonly RunningJobService Jobs;
        public readonly AsyncProxy Backup;
        public readonly AutomationService Service;
        public Fixture()
        {
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Jobs = new RunningJobService(store, NullLogger<RunningJobService>.Instance);
            var backup = DispatchProxy.Create<IBackupService, AsyncProxy>();
            Backup = (AsyncProxy)(object)backup;
            Service = new AutomationService(store, backup, Stub<ISslService>(), Stub<IMailService>(),
                Stub<IIisSiteService>(), Stub<IDnsService>(), Stub<IComponentBootstrapper>(), Stub<ISelfUpdateService>(),
                Stub<IDatabaseService>(), Stub<IBruteForceProtectionService>(), Stub<ISecretProtector>(),
                Stub<INotificationService>(), Stub<ISystemMonitoringService>(), Stub<IHealthService>(),
                Stub<ISchedulerService>(), Stub<IErrorReportSender>(), new AdaPanel.Infrastructure.Services.ConcurrencyLock(),
                Stub<AdaPanel.Infrastructure.Logs.ILogRotationService>(), Stub<IDatabaseOwnershipService>(), Jobs,
                NullLogger<AutomationService>.Instance);
        }
        public Task Configure() => store.WriteAsync("config/automation.json", new
        {
            autoBackupEnabled = true, backupSites = false, backupPanelConfig = true,
            autoSslRenewEnabled = false, updateCheckEnabled = false
        });
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private static T Stub<T>() where T : class => DispatchProxy.Create<T, AsyncProxy>();
    }

    public class AsyncProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Handler is not null) return Handler(method!, args);
            if (method!.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var type = method.ReturnType.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type)
                    .Invoke(null, [type.IsValueType ? Activator.CreateInstance(type) : null]);
            }
            throw new NotSupportedException(method.Name);
        }
    }
}
