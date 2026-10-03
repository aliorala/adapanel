using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class StalwartRestartVerificationTests
{
    [Theory]
    [InlineData("Ready")]
    [InlineData("Running")]
    [InlineData("Çalışıyor")]
    [InlineData("TaskName: C:\\Enabled-mail")]
    public async Task SchedulerSuccessWithoutAnObservedMailProcessIsNotSuccess(string schedulerText)
    {
        var f = new Fixture { Query = schedulerText };
        var result = await f.Start();
        Assert.NotNull(result);
        Assert.Equal("backup.mail_service_not_running", result.MessageKey);
        Assert.True(f.Probes > 1);
        Assert.True(f.Delays > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UnknownObservationCannotReportAHealthyRestart(int queryExit)
    {
        var f = new Fixture { QueryExit = queryExit, Observation = new(StalwartProcessState.Unknown, "pid 71: cannot read executable path") };
        var result = await f.Start();
        Assert.NotNull(result);
        Assert.Equal("backup.mail_service_start_unverified", result.MessageKey);
        Assert.Contains("pid 71", result.Detail);
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("TaskName: \\Disabled-Mail")]
    [InlineData("TaskName: \\Devre Disi klasör\\AdaStalwart")]
    public async Task ObservedRunningMailProcessWinsOverLocalizedSchedulerText(string schedulerText)
    {
        var f = new Fixture { Query = schedulerText, Observation = new(StalwartProcessState.Running) };
        Assert.Null(await f.Start());
        Assert.True(f.Probes > 0);
    }

    [Fact]
    public async Task DelayedStartIsObservedBeforeReturningSuccess()
    {
        var f = new Fixture { RunningAfter = 3 };
        Assert.Null(await f.Start());
        Assert.Equal(3, f.Probes);
        Assert.Equal(2, f.Delays);
    }

    [Fact]
    public async Task AlreadyRunningProcessStillCountsWhenRunCommandReportsAnError()
    {
        var f = new Fixture { RunExit = 1, Observation = new(StalwartProcessState.Running) };
        Assert.Null(await f.Start());
        Assert.True(f.Probes > 0);
    }

    [Fact]
    public async Task ConfirmedStoppedRuntimePreservesRunFailureDiagnostic()
    {
        var f = new Fixture { RunExit = 1 };
        var result = await f.Start();
        Assert.Equal("backup.mail_service_start_failed", result!.MessageKey);
        Assert.Contains("run denied", result.Detail);
        Assert.True(f.Probes > 0);
    }

    [Fact]
    public async Task ObservationExceptionIsUnverifiedNotHealthyOrMissing()
    {
        var f = new Fixture { ProbeError = new UnauthorizedAccessException("process inventory denied") };
        var result = await f.Start();
        Assert.Equal("backup.mail_service_start_unverified", result!.MessageKey);
        Assert.Contains("process inventory denied", result.Detail);
    }

    [Fact]
    public async Task RestartCommandsAndProbesReceiveACancellableToken()
    {
        var f = new Fixture { Observation = new(StalwartProcessState.Running) };
        await f.Start();
        Assert.True(f.CommandCanCancel);
        Assert.True(f.ProbeCanCancel);
    }

    [Fact]
    public async Task CanceledRecoveryDoesNotRunCommandsOrProbe()
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Start(cts.Token));
        Assert.Equal(0, f.Commands);
        Assert.Equal(0, f.Probes);
    }

    [Fact]
    public async Task CancellationDuringObservationStopsPolling()
    {
        using var cts = new CancellationTokenSource();
        var f = new Fixture { OnDelay = cts.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Start(cts.Token));
        Assert.Equal(1, f.Probes);
        Assert.Equal(1, f.Delays);
    }

    private sealed class Fixture
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-mail-start-fixture");
        public string Query = "Ready";
        public int QueryExit, RunExit, Commands, Probes, Delays;
        public int RunningAfter = int.MaxValue;
        public bool CommandCanCancel, ProbeCanCancel;
        public Exception? ProbeError;
        public Action? OnDelay;
        public StalwartProcessObservation Observation = new(StalwartProcessState.Stopped);

        public Task<AdaPanel.Core.Dtos.Backup.BackupRestoreWarningDto?> Start(CancellationToken ct = default) =>
            BackupService.StartStalwartAsync(ct, Command, Probe, Delay, root);
        private Task<CommandResult> Command(string file, string arguments, CancellationToken ct)
        {
            Commands++;
            CommandCanCancel = ct.CanBeCanceled;
            Assert.Equal("schtasks.exe", file);
            Assert.Contains("AdaStalwart", arguments);
            return Task.FromResult(arguments.StartsWith("/Run")
                ? new CommandResult { ExitCode = RunExit, StandardError = RunExit == 0 ? "" : "run denied" }
                : new CommandResult { ExitCode = QueryExit, StandardOutput = Query });
        }
        private Task<StalwartProcessObservation> Probe(string directory, CancellationToken ct)
        {
            Assert.Equal(root, directory);
            Probes++;
            ProbeCanCancel = ct.CanBeCanceled;
            if (ProbeError is not null) throw ProbeError;
            return Task.FromResult(Probes >= RunningAfter ? new(StalwartProcessState.Running) : Observation);
        }
        private Task Delay(TimeSpan amount, CancellationToken ct)
        {
            Assert.InRange(amount.TotalMilliseconds, 1, 1000);
            Assert.True(ct.CanBeCanceled);
            Delays++;
            OnDelay?.Invoke();
            return Task.CompletedTask;
        }
    }
}
