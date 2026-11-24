using System.Text.Json.Nodes;
using AdaPanel.Core.Dtos.Automation;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Logs;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Logs;
using AdaPanel.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Arka plan işçilerinin ve erişim kurtarma yardımcısının DAVRANIŞI: hata turu döngüyü
/// düşürmez, kapanış isteği döngüyü bitirir, kurtarma yalnız engel/deneme listesini boşaltır.
/// </summary>
public sealed class WorkerAndRecoveryBehaviorTests
{
    // ---------- AutomationWorker ----------

    [Fact]
    public async Task AutomationWorker_keeps_polling_after_a_failing_round()
    {
        var automation = new FlakyAutomation();
        var worker = NewAutomationWorker(automation);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            SpinWait.SpinUntil(() => automation.WatchdogRuns >= 3, TimeSpan.FromSeconds(10));
            // İlk tur patladı; döngü yaşadı ve sonraki turlar çalıştı.
            Assert.True(automation.WatchdogRuns >= 3);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AutomationWorker_stops_running_rounds_after_shutdown()
    {
        var automation = new FlakyAutomation();
        var worker = NewAutomationWorker(automation);
        await worker.StartAsync(CancellationToken.None);
        SpinWait.SpinUntil(() => automation.WatchdogRuns >= 2, TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);
        var afterStop = automation.WatchdogRuns;
        await Task.Delay(150);
        Assert.Equal(afterStop, automation.WatchdogRuns);
    }

    private static AutomationWorker NewAutomationWorker(IAutomationService automation)
    {
        var services = new ServiceCollection().AddScoped<IAutomationService>(_ => automation).BuildServiceProvider();
        return new AutomationWorker(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AutomationWorker>.Instance)
        {
            StartupDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(20)
        };
    }

    private sealed class FlakyAutomation : IAutomationService
    {
        public int WatchdogRuns;
        public Task RunWatchdogsAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref WatchdogRuns) == 1)
            {
                throw new InvalidOperationException("ilk tur bilerek patlar");
            }
            return Task.CompletedTask;
        }
        public Task RunLongJobsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RunDueJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutomationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutomationStatusDto> UpdateSettingsAsync(UpdateAutomationSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutomationStatusDto> RunBackupNowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutomationStatusDto> RunSslRenewNowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRemoteTestResultDto> TestRemoteBackupAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // ---------- LogRotationWorker ----------

    [Fact]
    public async Task LogRotationWorker_runs_rounds_repeatedly_even_when_a_round_throws()
    {
        var rotation = new FakeRotation { ThrowOnFirst = true };
        // PhpSiteLogMaintenance kayıtlı değil: her tur o noktada InvalidOperationException alır;
        // işçi bunu yutup bir sonraki tura geçmek ZORUNDA.
        var services = new ServiceCollection().AddScoped<ILogRotationService>(_ => rotation).BuildServiceProvider();
        var worker = new LogRotationWorker(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LogRotationWorker>.Instance)
        {
            IlkGecikme = TimeSpan.Zero,
            Aralik = TimeSpan.FromMilliseconds(20)
        };
        await worker.StartAsync(CancellationToken.None);
        try
        {
            SpinWait.SpinUntil(() => rotation.Calls >= 3, TimeSpan.FromSeconds(10));
            Assert.True(rotation.Calls >= 3);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LogRotationWorker_runs_no_round_when_shut_down_during_startup_delay()
    {
        var rotation = new FakeRotation();
        var services = new ServiceCollection().AddScoped<ILogRotationService>(_ => rotation).BuildServiceProvider();
        var worker = new LogRotationWorker(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LogRotationWorker>.Instance)
        {
            IlkGecikme = TimeSpan.FromMinutes(5)
        };
        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(0, rotation.Calls);
    }

    private sealed class FakeRotation : ILogRotationService
    {
        public int Calls;
        public bool ThrowOnFirst;
        public Task<bool> RunDueAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref Calls) == 1 && ThrowOnFirst)
            {
                throw new IOException("ilk tur bilerek patlar");
            }
            return Task.FromResult(false);
        }
        public Task<LogRotationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LogRotationStatusDto> UpdateSettingsAsync(UpdateLogRotationSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LogRotationStatusDto> RunAsync(string? siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LogRotationTargetDto> GetSiteStatusAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // ---------- BruteForceRecovery ----------

    [Fact]
    public async Task BruteForceRecovery_empties_bans_and_attempts_but_keeps_settings()
    {
        var path = Path.Combine(Path.GetTempPath(), "adapanel-bf-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Kural adı yok: güvenlik duvarına dokunulmaz, yalnız dosya yeniden yazılır.
            await File.WriteAllTextAsync(path,
                "{\"threshold\":7,\"banMinutes\":45,\"whitelist\":[\"10.0.0.1\"],"
                + "\"bans\":[{\"ip\":\"203.0.113.9\"}],\"attempts\":[{\"ip\":\"203.0.113.9\",\"count\":3}]}");

            await BruteForceRecovery.ClearBansAsync(path, NullLogger.Instance);

            var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert.Empty(node["bans"]!.AsArray());
            Assert.Empty(node["attempts"]!.AsArray());
            Assert.Equal(7, (int)node["threshold"]!);
            Assert.Equal(45, (int)node["banMinutes"]!);
            Assert.Equal("10.0.0.1", (string)node["whitelist"]![0]!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BruteForceRecovery_does_not_create_a_state_file_when_none_exists()
    {
        var path = Path.Combine(Path.GetTempPath(), "adapanel-bf-missing-" + Guid.NewGuid().ToString("N") + ".json");
        await BruteForceRecovery.ClearBansAsync(path, NullLogger.Instance);
        Assert.False(File.Exists(path));
    }

    // ---------- WebDbClientService ----------

    [Fact]
    public async Task WebDbClient_reports_the_deployed_port_per_provider_and_zero_when_missing()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "adapanel-webdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "config", "webdb.json"),
                "[{\"provider\":\"adminer\",\"port\":8897}]");
            var fileStore = new AdaPanel.Infrastructure.Support.JsonFileStore(Microsoft.Extensions.Options.Options.Create(
                new AdaPanel.Infrastructure.Options.PanelRuntimeOptions { DataRoot = dataRoot }));
            var service = new AdaPanel.Infrastructure.WebDbClient.WebDbClientService(
                null!, null!, null!, null!, null!, null!, null!, fileStore,
                NullLogger<AdaPanel.Infrastructure.WebDbClient.WebDbClientService>.Instance);

            Assert.Equal(8897, await service.GetDeployedToolPortAsync("ADMINER"));
            Assert.Equal(0, await service.GetDeployedToolPortAsync("phpmyadmin"));
        }
        finally
        {
            Directory.Delete(dataRoot, true);
        }
    }
}
