using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SchedulerMigrationFreshTests
{
    [Theory]
    [InlineData("hourly")]
    [InlineData("minute")]
    [InlineData("daily")]
    public async Task Public_create_uses_create_only_disabled_COM_and_validates_first_registration_marker(string schedule)
    {
        using var f = new Fixture(); var request = f.Request(); request.ScheduleType = schedule;
        var task = await f.Service.CreateTaskAsync(request, ScheduledTaskPrivilege.Full);
        Assert.Equal("Disabled", task.State); Assert.NotNull(f.CreateScript);
        var script = f.CreateScript!;
        Assert.Contains("$definition.Settings.Enabled = $false", script);
        Assert.Matches(@"RegisterTaskDefinition\('migration-job', \$definition, 10,", script);
        Assert.Contains("$read.RegistrationInfo.Source -ne", script);
        Assert.Contains("$read.Actions.Item(1).Arguments -cne", script);
        Assert.Contains("$read.Triggers.Count -ne 1", script);
        Assert.Contains("$read.Principal.RunLevel -ne 0", script);
        Assert.Contains("LOCAL SERVICE", script); Assert.DoesNotContain("'SYSTEM'", script);
        Assert.DoesNotContain("Register-ScheduledTask", script); Assert.DoesNotContain("Disable-ScheduledTask", script);
        Assert.DoesNotContain("Unregister", script); Assert.DoesNotContain("Start-ScheduledTask", script);
        Assert.DoesNotContain(".Run(", script); Assert.Equal(1, f.Creates);
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("error")]
    [InlineData("enabled")]
    [InlineData("missing")]
    public async Task Missing_or_untrusted_native_evidence_never_reports_success_or_deletes_partial_task(string failure)
    {
        using var f = new Fixture { Failure = failure };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.CreateTaskAsync(f.Request(), ScheduledTaskPrivilege.Full));
        Assert.StartsWith("migration.", error.MessageKey); Assert.Equal(1, f.Creates);
        Assert.DoesNotContain("SECRET", error.MessageKey); Assert.DoesNotContain("Unregister", f.CreateScript!);
    }

    [Fact]
    public async Task Existing_task_is_rejected_before_registration_and_preserved()
    {
        using var f = new Fixture { Existing = true };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.CreateTaskAsync(f.Request(), ScheduledTaskPrivilege.Full));
        Assert.Equal("migration.target_task_exists", error.MessageKey); Assert.Equal(0, f.Creates); Assert.Null(f.CreateScript);
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("type")]
    [InlineData("interval")]
    [InlineData("daily-interval")]
    [InlineData("time")]
    public async Task Migration_rejects_unrepresentable_settings_before_native_registration(string fault)
    {
        using var f = new Fixture(); var request = f.Request();
        if (fault == "schedule") request.ScheduleType = "weekly";
        if (fault == "type") request.TaskType = "linux-shell";
        if (fault == "interval") request.Interval = 0;
        if (fault == "daily-interval") { request.ScheduleType = "daily"; request.Interval = 7; }
        if (fault == "time") request.TimeOfDay = "25:00";
        await Assert.ThrowsAsync<AppException>(() => f.Service.CreateTaskAsync(request, ScheduledTaskPrivilege.Full));
        Assert.Equal(0, f.Creates);
    }

    [Fact]
    public void Public_JSON_cannot_select_internal_migration_flag_and_default_create_is_unchanged()
    {
        var request = JsonSerializer.Deserialize<CreateScheduledTaskRequest>("{\"MigrationFreshDisabled\":true}");
        Assert.False(request!.MigrationFreshDisabled);
        Assert.DoesNotContain("MigrationFreshDisabled", JsonSerializer.Serialize(new CreateScheduledTaskRequest { MigrationFreshDisabled = true }));
        var script = SchedulerService.BuildCreateScript(new() { Name = "ordinary", Command = "fixture.exe" }, null);
        Assert.Contains("Register-ScheduledTask", script); Assert.DoesNotContain("RegisterTaskDefinition", script);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ada-scheduler-migration-" + Guid.NewGuid().ToString("N"));
        internal SchedulerService Service;
        internal string Failure = "";
        internal bool Existing;
        internal int Creates;
        internal string? CreateScript;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Service = new(null!, Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }), NullLogger<SchedulerService>.Instance);
            Service.TaskCommand = (script, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (script.Contains("RegisterTaskDefinition", StringComparison.Ordinal))
                {
                    CreateScript = script; Creates++;
                    var marker = Regex.Match(script, "AdaPanel-Migration-[a-f0-9]{32}").Value;
                    return Task.FromResult(new CommandResult { ExitCode = Failure == "error" ? 1 : 0, StandardError = "SECRET",
                        StandardOutput = Failure == "marker" ? "success" : "ADAPANEL-MIGRATION-CREATED=" + marker });
                }
                if (script.Contains("Get-ScheduledTask", StringComparison.Ordinal))
                {
                    var tasks = Existing || Creates > 0 && Failure != "missing" ? new[] { new ScheduledTaskDto { Name = "migration-job", Command = "fixture.exe", WorkingDirectory = Root,
                        State = Failure == "enabled" ? "Ready" : "Disabled", RunAs = "limited", ScheduleType = "hourly", RepetitionInterval = "PT1H", ExecutionTimeLimit = "PT0S" } } : [];
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = JsonSerializer.Serialize(tasks) });
                }
                Assert.Contains("icacls.exe", script);
                return Task.FromResult(new CommandResult { ExitCode = 0 });
            };
        }
        internal CreateScheduledTaskRequest Request() => new() { Name = "migration-job", Command = "fixture.exe", WorkingDirectory = Root, MigrationFreshDisabled = true };
        public void Dispose() => Directory.Delete(Root, true);
    }
}
