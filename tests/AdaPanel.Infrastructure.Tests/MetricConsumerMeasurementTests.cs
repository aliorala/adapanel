using System.Reflection;
using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Advisor;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Cluster;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MetricConsumerMeasurementTests
{
    [Theory]
    [InlineData("memory-unmeasured")]
    [InlineData("memory-nan")]
    [InlineData("memory-infinite")]
    [InlineData("memory-out-of-range")]
    [InlineData("memory-invalid-capacity")]
    [InlineData("disk-unmeasured")]
    [InlineData("disk-nan")]
    [InlineData("disk-infinite")]
    [InlineData("disk-out-of-range")]
    [InlineData("disk-invalid-capacity")]
    [InlineData("disk-enumeration-unknown")]
    public async Task AdvisorMarksUnmeasuredCapacityAsAnIncompleteCheck(string scenario)
    {
        var system = Fake<ISystemMonitoringService>((_, _) => Task.FromResult(Overview(scenario)));
        var advisor = new AdvisorService(null!, system, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<AdvisorService>.Instance);

        var result = await advisor.GetReportAsync();

        Assert.Contains("system", result.FailedChecks);
        Assert.DoesNotContain(result.Items, item => item.Id == "memory-high" || item.Id.StartsWith("disk-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdvisorKeepsMeasuredWarningsWhenAnotherDiskCannotBeMeasured()
    {
        var overview = new SystemOverviewDto
        {
            MemoryMeasured = true, TotalMemoryBytes = 1000, AvailableMemoryBytes = 500, UsedMemoryBytes = 500, MemoryUsagePercent = 50,
            DisksMeasured = false, Disks = [Disk(96), new() { Name = "D:", Measured = false }]
        };
        var advisor = new AdvisorService(null!, Fake<ISystemMonitoringService>((_, _) => Task.FromResult(overview)),
            null!, null!, null!, null!, null!, null!, null!, NullLogger<AdvisorService>.Instance);
        var result = await advisor.GetReportAsync();
        Assert.Contains("system", result.FailedChecks);
        Assert.Single(result.Items, item => item.Id == "disk-critical:C:");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(95)]
    public async Task AdvisorUsesValidZeroAndHighMeasurements(double percentage)
    {
        var system = Fake<ISystemMonitoringService>((_, _) => Task.FromResult(Healthy(percentage)));
        var advisor = new AdvisorService(null!, system, null!, null!, null!, null!, null!, null!, null!, NullLogger<AdvisorService>.Instance);
        var result = await advisor.GetReportAsync();
        Assert.DoesNotContain("system", result.FailedChecks);
        Assert.Equal(percentage >= 90, result.Items.Any(item => item.Id == "memory-high"));
        Assert.Equal(percentage >= 90, result.Items.Any(item => item.Id.StartsWith("disk-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("memory-unmeasured")]
    [InlineData("memory-infinite")]
    [InlineData("memory-out-of-range")]
    [InlineData("memory-invalid-capacity")]
    [InlineData("disk-unmeasured")]
    [InlineData("disk-infinite")]
    [InlineData("disk-out-of-range")]
    [InlineData("disk-invalid-capacity")]
    public async Task WorkerDoesNotSendCapacityAlertsOrCreateCooldownForInvalidMeasurements(string scenario)
    {
        using var fixture = new WorkerFixture(Overview(scenario));
        await fixture.Configure();
        await fixture.Service.RunDueJobsAsync();
        Assert.Empty(fixture.Notifications);
        var cooldown = await fixture.Store.ReadAsync("config/automation-alerts.json", new Dictionary<string, DateTime>());
        Assert.DoesNotContain(cooldown.Keys, key => key == "memory" || key.StartsWith("disk:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerStillAlertsOnMeasuredCapacityAndSuppressesItsSecondDelivery()
    {
        using var fixture = new WorkerFixture(Healthy(95));
        await fixture.Configure();
        await fixture.Service.RunDueJobsAsync();
        Assert.Equal(2, fixture.Notifications.Count);
        Assert.Contains(fixture.Notifications, subject => subject.Contains("RAM", StringComparison.Ordinal));
        Assert.Contains(fixture.Notifications, subject => subject.Contains("C:", StringComparison.Ordinal));
        await fixture.Service.RunDueJobsAsync();
        Assert.Equal(2, fixture.Notifications.Count);
    }

    [Theory]
    [InlineData("disk-unmeasured")]
    [InlineData("disk-nan")]
    [InlineData("disk-infinite")]
    [InlineData("disk-out-of-range")]
    [InlineData("disk-invalid-capacity")]
    [InlineData("disk-enumeration-unknown")]
    [InlineData("disk-empty")]
    public void PlacementCannotPreferUnmeasuredCapacityBecauseItsSiteCountIsLower(string scenario)
    {
        var unsafeNode = Node("unknown", 0, Overview(scenario));
        var healthyNode = Node("healthy", 10, Healthy(50));
        var result = ClusterPlacement.Select([unsafeNode, healthyNode]);
        Assert.Equal(healthyNode.Node.Id, result.NodeId);
        Assert.Equal(1, result.CandidateCount);
        Assert.Null(ClusterPlacement.Select([unsafeNode]).NodeId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(95)]
    public void PlacementAcceptsMeasuredCapacityWithoutChangingItsSiteCountOrdering(double percentage)
    {
        var selected = Node("selected", 1, Healthy(percentage));
        var result = ClusterPlacement.Select([Node("other", 10, Healthy(0)), selected]);
        Assert.Equal(selected.Node.Id, result.NodeId);
        Assert.Equal(2, result.CandidateCount);
    }

    private static SystemOverviewDto Overview(string scenario)
    {
        var memory = scenario.StartsWith("memory-", StringComparison.Ordinal);
        var percentage = scenario.EndsWith("nan", StringComparison.Ordinal) ? double.NaN :
            scenario.EndsWith("infinite", StringComparison.Ordinal) ? double.PositiveInfinity :
            scenario.EndsWith("out-of-range", StringComparison.Ordinal) ? 101d : 99d;
        var invalidCapacity = scenario.EndsWith("invalid-capacity", StringComparison.Ordinal);
        return new SystemOverviewDto
        {
            MemoryMeasured = scenario != "memory-unmeasured", TotalMemoryBytes = memory && invalidCapacity ? 0 : 1000,
            AvailableMemoryBytes = memory ? 10 : 500, UsedMemoryBytes = memory ? 990 : 500,
            MemoryUsagePercent = memory ? percentage : 50,
            DisksMeasured = scenario is not ("disk-unmeasured" or "disk-enumeration-unknown"),
            Disks = scenario == "disk-empty" ? [] : [new DiskUsageDto
            {
                Name = "C:", Measured = scenario != "disk-unmeasured", TotalBytes = !memory && invalidCapacity ? 0 : 1000,
                FreeBytes = memory ? 500 : 10, UsedBytes = memory ? 500 : 990,
                UsagePercent = memory ? 50 : scenario == "disk-enumeration-unknown" ? 50 : percentage
            }]
        };
    }

    private static SystemOverviewDto Healthy(double percentage) => new()
    {
        MemoryMeasured = true, TotalMemoryBytes = 1000, AvailableMemoryBytes = (long)(1000 - percentage * 10),
        UsedMemoryBytes = (long)(percentage * 10), MemoryUsagePercent = percentage,
        DisksMeasured = true, Disks = [Disk(percentage)]
    };
    private static DiskUsageDto Disk(double percentage) => new()
    {
        Name = "C:", Measured = true, TotalBytes = 1000, FreeBytes = (long)(1000 - percentage * 10),
        UsedBytes = (long)(percentage * 10), UsagePercent = percentage
    };
    private static ClusterNodeStatusDto Node(string label, int count, SystemOverviewDto overview) => new()
    {
        Node = new() { Id = Guid.NewGuid(), Label = label, Enabled = true }, Reachable = true,
        Info = new() { SiteCount = count, Overview = overview }
    };

    private sealed class WorkerFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-metric-consumers-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider provider;
        public JsonFileStore Store { get; }
        public AutomationService Service { get; }
        public List<string> Notifications { get; } = [];
        public WorkerFixture(SystemOverviewDto overview)
        {
            Directory.CreateDirectory(root);
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var dependencies = new Dictionary<Type, object>
            {
                [typeof(JsonFileStore)] = Store,
                [typeof(IConcurrencyLock)] = new ConcurrencyLock(),
                [typeof(ILogger<AutomationService>)] = NullLogger<AutomationService>.Instance,
                [typeof(IRunningJobService)] = new AdaPanel.Infrastructure.RunningJobs.RunningJobService(Store,
                    NullLogger<AdaPanel.Infrastructure.RunningJobs.RunningJobService>.Instance),
                [typeof(ISystemMonitoringService)] = Fake<ISystemMonitoringService>((method, _) => method.Name switch
                {
                    "GetOverviewAsync" => Task.FromResult(overview), "RecordSampleAsync" => Task.CompletedTask,
                    _ => throw new NotSupportedException(method.Name)
                }),
                [typeof(INotificationService)] = Fake<INotificationService>((method, args) => method.Name switch
                {
                    "IsEmailConfiguredAsync" => Task.FromResult(true),
                    "GetSettingsAsync" => Task.FromResult(new NotificationSettingsDto()),
                    "SendEmailAsync" => Notify((string)args[0]!),
                    _ => throw new NotSupportedException(method.Name)
                })
            };
            var services = new ServiceCollection();
            foreach (var parameter in typeof(AutomationService).GetConstructors().Single().GetParameters())
                if (!dependencies.ContainsKey(parameter.ParameterType))
                    dependencies[parameter.ParameterType] = DispatchProxy.Create(parameter.ParameterType, typeof(AutomationRunningJobTests.AsyncProxy));
            foreach (var dependency in dependencies) services.AddSingleton(dependency.Key, dependency.Value);
            services.AddTransient<AutomationService>();
            provider = services.BuildServiceProvider();
            Service = provider.GetRequiredService<AutomationService>();
        }
        private Task Notify(string subject) { Notifications.Add(subject); return Task.CompletedTask; }
        public Task Configure() => Store.WriteAsync("config/automation.json", new
        {
            autoBackupEnabled = false, alertsEnabled = true, serviceDownAlertsEnabled = false,
            autoSslRenewEnabled = false, updateCheckEnabled = false, logCleanupEnabled = false,
            diskUsageThresholdPercent = 90, memoryUsageThresholdPercent = 90
        });
        public void Dispose()
        {
            provider.Dispose();
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-metric-consumers-"), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
        }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)result).Handler = handler;
        return result;
    }
}
