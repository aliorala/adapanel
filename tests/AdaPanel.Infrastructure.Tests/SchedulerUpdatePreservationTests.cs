using System.Reflection;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SchedulerUpdatePreservationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task Limited_task_remains_runnable_when_log_permission_grant_fails(int aclExitCode)
    {
        using var fixture = new Fixture(existing: true);
        var request = fixture.Request();
        var log = await File.ReadAllBytesAsync(fixture.Output);
        var commands = fixture.Service.TaskCommand!;
        fixture.Service.TaskCommand = (script, token) =>
            script.Contains("& icacls.exe", StringComparison.Ordinal)
                ? Task.FromResult(new CommandResult { ExitCode = aclExitCode })
                : commands(script, token);
        var task = await fixture.Service.CreateTaskAsync(request, ScheduledTaskPrivilege.Limited, fixture.Token);
        Assert.Equal(request.Command, task.Command);
        Assert.Equal(request.Arguments, task.Arguments);
        Assert.Equal("limited", task.RunAs);
        Assert.Equal(aclExitCode == 0, task.CapturesOutput);
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.State));
        var saved = state.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("TaskName").GetString() == request.Name);
        var action = saved.GetProperty("Actions")[0];
        Assert.Equal(aclExitCode == 0 ? "cmd.exe" : request.Command, action.GetProperty("Execute").GetString());
        Assert.Equal(log, await File.ReadAllBytesAsync(fixture.Output));
    }

    [Theory]
    [InlineData("action")]
    [InlineData("trigger")]
    [InlineData("principal")]
    [InlineData("settings")]
    [InlineData("register")]
    public async Task AFaultWhileEditingDoesNotDeleteThePreviouslyWorkingTask(string phase)
    {
        using var fixture = new Fixture(existing: true) { Failure = phase };
        var before = await File.ReadAllBytesAsync(fixture.State);
        var log = await File.ReadAllBytesAsync(fixture.Output);
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.CreateTaskAsync(
            fixture.Request(), ScheduledTaskPrivilege.Full, fixture.Token));
        Assert.Equal("cron.write_failed", error.MessageKey);
        Assert.Contains("fixture " + phase + " failure", error.Details, StringComparison.Ordinal);
        Assert.Contains(Fixture.TaskName, fixture.TaskNames());
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.State));
        Assert.Equal(log, await File.ReadAllBytesAsync(fixture.Output));
        Assert.Contains(phase, await fixture.ReadEvents());
        Assert.Equal(1, fixture.Commands); // Failure must not be converted into a successful readback.
    }

    [Fact]
    public async Task CancellationWhileRegisteringKeepsTheOldTaskAndItsOutput()
    {
        using var fixture = new Fixture(existing: true) { Failure = "cancel-register" };
        var before = await File.ReadAllBytesAsync(fixture.State);
        var log = await File.ReadAllBytesAsync(fixture.Output);
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Service.CreateTaskAsync(fixture.Request(), ScheduledTaskPrivilege.Full, cancellation.Token);
        Exception? waitError = null;
        try
        {
            // Wait for the actual generated script to reach the stub registration call;
            // a fixed timer could cancel before the destructive baseline boundary.
            while (!File.Exists(fixture.Entered) && !pending.IsCompleted)
                await Task.Delay(20, fixture.Token);
        }
        catch (Exception error) { waitError = error; }
        finally { cancellation.Cancel(); }
        // Always observe termination before owned-directory cleanup, even if setup or
        // the barrier failed. An early script error must not leave a process behind.
        var failure = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(waitError);
        Assert.True(File.Exists(fixture.Entered), "Registration must be reached before cancelling: " + failure);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Contains(Fixture.TaskName, fixture.TaskNames());
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.State));
        Assert.Equal(log, await File.ReadAllBytesAsync(fixture.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidCreateAndUpdateReturnThePersistedDefinition(bool existing)
    {
        using var fixture = new Fixture(existing);
        var other = fixture.OtherTaskBytes();
        var request = fixture.Request();
        var result = await fixture.Service.CreateTaskAsync(request, ScheduledTaskPrivilege.Full, fixture.Token);
        Assert.Equal(Fixture.TaskName, result.Name);
        Assert.Equal("Ready", result.State);
        Assert.Equal(request.Command, result.Command);
        Assert.Equal(request.Arguments, result.Arguments);
        Assert.Equal(request.WorkingDirectory, result.WorkingDirectory);
        Assert.Equal("daily", result.ScheduleType);
        Assert.Equal("03:25", result.TimeOfDay);
        Assert.Equal("system", result.RunAs);
        Assert.Equal(2, result.TimeLimitHours);
        Assert.True(result.CapturesOutput);
        Assert.Equal(other, fixture.OtherTaskBytes());
        Assert.Equal(2, fixture.Commands); // Real service creates, then reads the saved native-shaped record.
    }

    [Fact]
    public async Task FailedNewRegistrationDoesNotCreateAPhantomTaskOrModifyANeighbor()
    {
        using var fixture = new Fixture(existing: false) { Failure = "register" };
        var before = await File.ReadAllBytesAsync(fixture.State);
        await Assert.ThrowsAsync<AppException>(() => fixture.Service.CreateTaskAsync(
            fixture.Request(), ScheduledTaskPrivilege.Full, fixture.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.State));
    }

    [Fact]
    public async Task InvalidEditStopsBeforeTheNativeBoundaryAndPreservesTheExistingTask()
    {
        using var fixture = new Fixture(existing: true);
        var before = await File.ReadAllBytesAsync(fixture.State);
        var request = fixture.Request();
        request.WorkingDirectory = Path.Combine(fixture.Root, "missing");
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.CreateTaskAsync(
            request, ScheduledTaskPrivilege.Full, fixture.Token));
        Assert.Equal("cron.working_directory_missing", error.MessageKey);
        Assert.Equal(0, fixture.Commands);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.State));
    }

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-scheduler-update-";
        internal const string TaskName = "nightly-backup";
        internal string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        internal string State => Path.Combine(Root, "tasks.json");
        internal string Output => Path.Combine(Root, "logs", "tasks", SchedulerService.ResolveLogFileName(TaskName));
        internal string Entered => Path.Combine(Root, "registration-entered");
        private string Events => Path.Combine(Root, "events.txt");
        internal string Failure { get; init; } = string.Empty;
        internal SchedulerService Service { get; }
        internal int Commands { get; private set; }
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(40));
        internal CancellationToken Token => deadline.Token;

        internal Fixture(bool existing)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
            File.WriteAllText(Output, "previous successful execution output");
            var taskNames = existing ? new[] { TaskName, "untouched-neighbor" } : new[] { "untouched-neighbor" };
            File.WriteAllText(State, JsonSerializer.Serialize(taskNames.Select(name => new
            {
                TaskName = name, TaskPath = "\\AdaPanel\\", State = "Ready",
                Actions = new[] { new { Execute = "old-worker.exe", Arguments = "--old", WorkingDirectory = Root } },
                Triggers = new[] { new { CimClass = new { CimClassName = "MSFT_TaskDailyTrigger" },
                    StartBoundary = "2026-10-04T01:15:00", Repetition = (object?)null } },
                Principal = new { UserId = "SYSTEM", RunLevel = "Highest" },
                Settings = new { ExecutionTimeLimit = "PT4H", StartWhenAvailable = true }
            })));
            var sites = DispatchProxy.Create<IIisSiteService, SubscriptionMeasurementProxy>();
            ((SubscriptionMeasurementProxy)(object)sites).Handler = (method, _) =>
                throw new InvalidOperationException("The command-only fixture must not access IIS: " + method.Name);
            Service = new SchedulerService(sites,
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }),
                NullLogger<SchedulerService>.Instance) { TaskCommand = RunFakePowerShell };
        }

        internal CreateScheduledTaskRequest Request() => new()
        {
            Name = TaskName, TaskType = "command", Command = Path.Combine(Root, "new-worker.exe"),
            Arguments = "--new", WorkingDirectory = Root, ScheduleType = "daily", TimeOfDay = "03:25",
            RunAs = "system", TimeLimitHours = 2
        };

        internal async Task<string[]> ReadEvents() => await File.ReadAllLinesAsync(Events);
        internal string[] TaskNames()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(State));
            return json.RootElement.EnumerateArray().Select(task => task.GetProperty("TaskName").GetString()!).ToArray();
        }
        internal string OtherTaskBytes()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(State));
            var other = json.RootElement.EnumerateArray().Single(task => task.GetProperty("TaskName").GetString() == "untouched-neighbor");
            // PowerShell may reformat the JSON array while replacing the other record.
            return JsonSerializer.Serialize(other);
        }

        private async Task<CommandResult> RunFakePowerShell(string script, CancellationToken token)
        {
            ValidateOwnedRoot();
            Commands++;
            var encodedScript = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            // Every Scheduler cmdlet resolves to a local function. AST allowlisting of
            // the production fragment rejects unexpected programs/dynamic invocation,
            // and module autoloading is disabled before it can execute. Only the JSON
            // state beneath this exact owned root is mutated; no task is ever scheduled.
            var harness = $$"""
                Import-Module Microsoft.PowerShell.Utility
                $PSModuleAutoLoadingPreference = 'None'
                $script:statePath = '{{Quote(State)}}'
                $script:eventsPath = '{{Quote(Events)}}'
                $script:enteredPath = '{{Quote(Entered)}}'
                $script:failure = '{{Quote(Failure)}}'
                $parsedTasks = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($script:statePath))
                # Explicitly enumerate the parsed array. In Windows PowerShell 5.1,
                # capturing ConvertFrom-Json's pipeline output can nest a wrapped array.
                $script:tasks = @(foreach ($parsedTask in $parsedTasks) { $parsedTask })
                function Save-FixtureTasks {
                  $rows = @(foreach ($row in $script:tasks) { ConvertTo-Json -InputObject $row -Depth 12 -Compress })
                  [System.IO.File]::WriteAllText($script:statePath, ('[' + ($rows -join ',') + ']'))
                }
                function Invoke-FixtureStep { param([string]$Name)
                  [System.IO.File]::AppendAllText($script:eventsPath, $Name + [Environment]::NewLine)
                  if ($script:failure -eq $Name) { throw ('fixture ' + $Name + ' failure') }
                  if ($script:failure -eq ('cancel-' + $Name)) {
                    [System.IO.File]::WriteAllText($script:enteredPath, $Name)
                    [System.Threading.Thread]::Sleep(30000)
                  }
                }
                function Assert-FixtureTask { param($TaskPath,$TaskName)
                  if ($TaskPath -ne '\AdaPanel\' -or ($TaskName -and $TaskName -ne 'nightly-backup')) { throw 'fixture task boundary violated' }
                }
                function Get-ScheduledTask { param($TaskPath,$TaskName,$ErrorAction)
                  Assert-FixtureTask $TaskPath $TaskName
                  foreach ($task in $script:tasks) { if (-not $TaskName -or $task.TaskName -eq $TaskName) { $task } }
                }
                function Get-ScheduledTaskInfo { param($TaskPath,$TaskName,$ErrorAction)
                  [pscustomobject]@{ LastTaskResult=0; LastRunTime=$null; NextRunTime=$null }
                }
                function Unregister-ScheduledTask { param($TaskPath,$TaskName,$Confirm,$ErrorAction)
                  Assert-FixtureTask $TaskPath $TaskName
                  Invoke-FixtureStep 'unregister'
                  $script:tasks = @($script:tasks | Where-Object { $_.TaskName -ne $TaskName })
                  Save-FixtureTasks
                }
                function New-ScheduledTaskAction { param($Execute,$Argument,$WorkingDirectory)
                  Invoke-FixtureStep 'action'
                  [pscustomobject]@{ Execute=$Execute; Arguments=$Argument; WorkingDirectory=$WorkingDirectory }
                }
                function New-ScheduledTaskTrigger { param([switch]$Once,[switch]$Daily,$At,$RepetitionInterval,$RepetitionDuration)
                  Invoke-FixtureStep 'trigger'
                  [pscustomobject]@{ CimClass=[pscustomobject]@{CimClassName='MSFT_TaskDailyTrigger'}; StartBoundary=$At.ToString('o'); Repetition=$null }
                }
                function New-ScheduledTaskPrincipal { param($UserId,$LogonType,$RunLevel)
                  Invoke-FixtureStep 'principal'
                  [pscustomobject]@{UserId=$UserId; LogonType=$LogonType; RunLevel=$RunLevel}
                }
                function New-ScheduledTaskSettingsSet { param([switch]$AllowStartIfOnBatteries,[switch]$DontStopIfGoingOnBatteries,[switch]$StartWhenAvailable,$MultipleInstances,$ExecutionTimeLimit)
                  Invoke-FixtureStep 'settings'
                  [pscustomobject]@{ExecutionTimeLimit=[System.Xml.XmlConvert]::ToString([TimeSpan]$ExecutionTimeLimit); StartWhenAvailable=$StartWhenAvailable.IsPresent}
                }
                function Register-ScheduledTask { param($TaskPath,$TaskName,$Action,$Trigger,$Principal,$Settings,[switch]$Force)
                  Assert-FixtureTask $TaskPath $TaskName
                  Invoke-FixtureStep 'register'
                  $script:tasks = @($script:tasks | Where-Object { $_.TaskName -ne $TaskName }) + @([pscustomobject]@{
                    TaskPath=$TaskPath; TaskName=$TaskName; State='Ready'; Actions=@($Action); Triggers=@($Trigger); Principal=$Principal; Settings=$Settings
                  })
                  Save-FixtureTasks
                }
                $nativeNames = @('Get-ScheduledTask','Get-ScheduledTaskInfo','Unregister-ScheduledTask','New-ScheduledTaskAction','New-ScheduledTaskTrigger','New-ScheduledTaskPrincipal','New-ScheduledTaskSettingsSet','Register-ScheduledTask')
                foreach ($name in $nativeNames) {
                  if ((Get-Command $name).CommandType -ne 'Function') { throw ('unsafe native command resolution: ' + $name) }
                }
                $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encodedScript}}'))
                $block = [ScriptBlock]::Create($source)
                $allowed = $nativeNames + @('Out-Null','Get-Date','New-TimeSpan','ForEach-Object','ConvertTo-Json')
                foreach ($command in $block.Ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)) {
                  $name = $command.GetCommandName()
                  if (-not $name -or $allowed -notcontains $name) { throw ('unsafe production command in fixture: ' + $name) }
                }
                & $block
                """;
            return await ProcessRunner.RunPowerShellAsync(harness, token);
        }

        private static string Quote(string value) => value.Replace("'", "''");
        private void ValidateOwnedRoot()
        {
            var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Assert.Equal(parent, Path.GetDirectoryName(actual), ignoreCase: true);
            var name = Path.GetFileName(actual);
            Assert.StartsWith(Prefix, name, StringComparison.Ordinal);
            Assert.True(Guid.TryParseExact(name[Prefix.Length..], "N", out _));
            Assert.False(File.GetAttributes(actual).HasFlag(FileAttributes.ReparsePoint));
        }
        public void Dispose()
        {
            deadline.Dispose();
            ValidateOwnedRoot();
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories),
                path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
            Directory.Delete(Root, recursive: true);
        }
    }
}
