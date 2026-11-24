using System.Reflection;
using System.Text.Json.Nodes;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Logs;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Logs;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.RunningJobs;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AutomationPendingJobSettingsTests
{
    [Theory]
    [InlineData(false, "disabled")]
    [InlineData(true, "disabled")]
    [InlineData(false, "new-result")]
    [InlineData(true, "new-result")]
    [InlineData(false, "enabled")]
    [InlineData(true, "enabled")]
    public async Task Pending_log_cleanup_uses_settings_and_results_after_previous_job(bool ssl, string change)
    {
        using var f = new Fixture();
        await f.Configure(backup: !ssl, ssl: ssl, logs: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        f.Backup.Handler = (method, _) => method.Name == nameof(IBackupService.PruneAsync)
            ? WaitBackup() : Task.FromResult(AutomationBackupOutcomeTests.Result("panel", "panel", null));
        async Task<BackupPruneResultDto> WaitBackup() { entered.TrySetResult(); await release.Task; return new(); }
        f.Ssl.Handler = (_, _) => WaitSsl();
        async Task<RenewAllResultDto> WaitSsl() { entered.TrySetResult(); await release.Task; return new() { MailSynchronizationPerformed = true }; }
        f.Logs.Handler = (_, _) => { calls++; return Task.FromResult(new LogRotationStatusDto()); };
        var run = f.Service.RunDueJobsAsync();
        var newer = DateTime.UtcNow;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Change(state =>
            {
                if (change == "disabled") state["logCleanupEnabled"] = false;
                if (change == "enabled") state["logRetentionDays"] = 17;
                if (change == "new-result")
                {
                    state["lastLogCleanupRunUtc"] = newer;
                    state["lastLogCleanupStatus"] = "new-result";
                    state["lastLogCleanupDetails"] = "preserve this result";
                }
            });
        }
        finally { release.TrySetResult(); await run; }
        var status = await f.Service.GetStatusAsync();
        Assert.Equal(change == "enabled" ? 1 : 0, calls);
        if (change == "new-result")
        {
            Assert.Equal(newer, status.LastLogCleanupRunUtc);
            Assert.Equal("new-result", status.LastLogCleanupStatus);
            Assert.Equal("preserve this result", status.LastLogCleanupDetails);
        }
        else if (change == "enabled")
        {
            Assert.Equal("completed", status.LastLogCleanupStatus);
            Assert.Contains("17-day value", status.LastLogCleanupDetails);
        }
        else Assert.Null(status.LastLogCleanupRunUtc);
    }

    [Theory]
    [InlineData(false, "updateCheckEnabled")]
    [InlineData(false, "autoApplyComponentUpdates")]
    [InlineData(true, "updateCheckEnabled")]
    [InlineData(true, "autoApplyComponentUpdates")]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    public async Task Pending_component_installs_recheck_permission_after_catalog_and_each_install(bool duringInstall, string disabled)
    {
        using var f = new Fixture();
        await f.Configure(updates: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installs = new List<string>();
        f.Components.Handler = (method, args) => method.Name == nameof(IComponentBootstrapper.CheckForUpdatesAsync)
            ? Catalog() : Install((string)args![0]!);
        async Task<SoftwareCatalogDto> Catalog()
        {
            if (!duringInstall) { entered.TrySetResult(); await release.Task; }
            return new() { Installed = [Component("php-8.3"), Component("adminer")] };
        }
        async Task<ComponentInstallResultDto> Install(string id)
        {
            installs.Add(id);
            if (duringInstall && installs.Count == 1) { entered.TrySetResult(); await release.Task; }
            return new() { ComponentId = id, Status = "Installed", Version = "2.0", VersionMeasured = true };
        }
        var run = f.Service.RunDueJobsAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (disabled != "unchanged") await f.Change(state => state[disabled] = false);
        }
        finally { release.TrySetResult(); await run; }
        var expected = disabled == "unchanged" ? 2 : duringInstall ? 1 : 0;
        Assert.Equal(expected, installs.Count);
        var status = await f.Service.GetStatusAsync();
        Assert.Equal(2 - expected, status.PendingComponentUpdates);
        Assert.Equal(expected > 0 ? "applied" : "completed", status.LastUpdateCheckStatus);
        Assert.Contains($"{expected} update(s) installed, {2 - expected} pending", status.LastUpdateCheckDetails);
    }

    private static InstalledComponentDto Component(string id) => new()
    {
        ComponentId = id, DisplayName = id, Status = "Installed", Version = "1.0",
        AvailableVersion = "2.0", UpdateAvailable = true, VersionCheckSupported = true
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-pending-settings-" + Guid.NewGuid().ToString("N"));
        private readonly JsonFileStore store;
        public readonly AutomationRunningJobTests.AsyncProxy Backup;
        public readonly AutomationRunningJobTests.AsyncProxy Ssl;
        public readonly AutomationRunningJobTests.AsyncProxy Logs;
        public readonly AutomationRunningJobTests.AsyncProxy Components;
        public readonly AutomationService Service;
        public Fixture()
        {
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var backup = Stub<IBackupService>(); Backup = (AutomationRunningJobTests.AsyncProxy)(object)backup;
            var ssl = Stub<ISslService>(); Ssl = (AutomationRunningJobTests.AsyncProxy)(object)ssl;
            var logs = Stub<ILogRotationService>(); Logs = (AutomationRunningJobTests.AsyncProxy)(object)logs;
            var components = Stub<IComponentBootstrapper>(); Components = (AutomationRunningJobTests.AsyncProxy)(object)components;
            Service = new AutomationService(store, backup, ssl, Stub<IMailService>(), Stub<IIisSiteService>(),
                Stub<IDnsService>(), components, Stub<ISelfUpdateService>(), Stub<IDatabaseService>(),
                Stub<IBruteForceProtectionService>(), Stub<ISecretProtector>(), Stub<INotificationService>(),
                Stub<ISystemMonitoringService>(), Stub<IHealthService>(), Stub<ISchedulerService>(),
                Stub<IErrorReportSender>(), new ConcurrencyLock(), logs, Stub<IDatabaseOwnershipService>(),
                new RunningJobService(store, NullLogger<RunningJobService>.Instance), NullLogger<AutomationService>.Instance);
        }
        public Task Configure(bool backup = false, bool ssl = false, bool logs = false, bool updates = false) =>
            store.WriteAsync("config/automation.json", new
            {
                autoBackupEnabled = backup, backupSites = false, backupPanelConfig = true,
                autoSslRenewEnabled = ssl, logCleanupEnabled = logs,
                updateCheckEnabled = updates, autoApplyComponentUpdates = updates
            });
        public Task Change(Action<JsonObject> change) => store.UpdateAsync("config/automation.json", new JsonObject(), state => { change(state); return state; });
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private static T Stub<T>() where T : class => DispatchProxy.Create<T, AutomationRunningJobTests.AsyncProxy>();
    }
}

internal sealed class AutomationUpdateSettingsStore : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-update-settings-" + Guid.NewGuid().ToString("N"));
    public JsonFileStore Store { get; }
    public AutomationUpdateSettingsStore()
    {
        Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
    }
    public Task EnableAsync() => Store.WriteAsync("config/automation.json", new { updateCheckEnabled = true, autoApplyComponentUpdates = true });
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
