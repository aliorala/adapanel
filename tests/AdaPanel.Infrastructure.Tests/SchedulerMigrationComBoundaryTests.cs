using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SchedulerMigrationComBoundaryTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("marker", false)]
    [InlineData("action", false)]
    [InlineData("principal", false)]
    [InlineData("schedule", false)]
    [InlineData("enabled", false)]
    [InlineData("collision", false)]
    public async Task Generated_native_script_executes_only_against_private_fake_COM_and_rejects_mismatched_readback(string fault, bool expected)
    {
        var request = new CreateScheduledTaskRequest { Name = "fixture-only", Command = "fixture.exe", Arguments = "--safe", ScheduleType = "hourly", Interval = 2, MigrationFreshDisabled = true };
        var script = SchedulerService.BuildMigrationCreateScript(request, new("fixture.exe", "--safe", "", ScriptTaskPlanner.LimitedUserId), null, "AdaPanel-Migration-fixture");
        // New-Object is shadowed before the generated script. No machine COM server or task is touched.
        var prefix = """
            $ErrorActionPreference = 'Stop'
            $script:fault = '__FAULT__'
            $script:task = $null
            function New-FixtureCollection {
              $collection = [pscustomobject]@{ Count=0; Value=$null }
              $collection | Add-Member ScriptMethod Create {
                param($type)
                $this.Value = [pscustomobject]@{ Type=$type; Path=''; Arguments=''; WorkingDirectory=''; StartBoundary=''; DaysInterval=0; Enabled=$false; Repetition=[pscustomobject]@{Interval='';Duration='';StopAtDurationEnd=$false} }
                $this.Count=1
                return $this.Value
              }
              $collection | Add-Member ScriptMethod Item { param($index) if ($index -ne 1) { throw 'index' }; return $this.Value }
              return $collection
            }
            $script:folder = [pscustomobject]@{}
            $script:folder | Add-Member ScriptMethod RegisterTaskDefinition {
              param($name,$definition,$flags,$user,$password,$logon,$sddl)
              if ($name -ne 'fixture-only' -or $flags -ne 10 -or $definition.Settings.Enabled -ne $false -or $definition.Principal.RunLevel -ne 0 -or $logon -ne 5) { throw 'unsafe registration contract' }
              if ($script:fault -eq 'collision') { throw 'already exists; TASK_CREATE cannot update' }
              $copy = ConvertFrom-Json ($definition | ConvertTo-Json -Depth 20)
              $copy.Actions | Add-Member ScriptMethod Item { param($index) return $this.Value }
              $copy.Triggers | Add-Member ScriptMethod Item { param($index) return $this.Value }
              if ($script:fault -eq 'marker') { $copy.RegistrationInfo.Source='other-registration' }
              if ($script:fault -eq 'action') { $copy.Actions.Value.Arguments='tampered' }
              if ($script:fault -eq 'principal') { $copy.Principal.UserId='SYSTEM' }
              if ($script:fault -eq 'schedule') { $copy.Triggers.Value.Repetition.Interval='PT3H' }
              if ($script:fault -eq 'enabled') { $copy.Settings.Enabled=$true }
              $script:task = [pscustomobject]@{ Definition=$copy; State=1 }
              return $script:task
            }
            $script:folder | Add-Member ScriptMethod GetTask { param($name) if ($name -ne 'fixture-only') { throw 'name' }; return $script:task }
            $script:service = [pscustomobject]@{}
            $script:service | Add-Member ScriptMethod Connect {}
            $script:service | Add-Member ScriptMethod GetFolder { param($path) if ($path -ne '\AdaPanel') { throw 'folder' }; return $script:folder }
            $script:service | Add-Member ScriptMethod NewTask {
              param($flags)
              return [pscustomobject]@{
                RegistrationInfo=[pscustomobject]@{Source='';Description=''}
                Settings=[pscustomobject]@{Enabled=$true;DisallowStartIfOnBatteries=$true;StopIfGoingOnBatteries=$true;StartWhenAvailable=$false;MultipleInstances=0;ExecutionTimeLimit=''}
                Principal=[pscustomobject]@{UserId='';LogonType=0;RunLevel=1}
                Actions=(New-FixtureCollection)
                Triggers=(New-FixtureCollection)
              }
            }
            function New-Object { param([string]$ComObject) if ($ComObject -ne 'Schedule.Service') { throw 'COM denied' }; return $script:service }
            """.Replace("__FAULT__", fault, StringComparison.Ordinal);
        var result = await ProcessRunner.RunPowerShellAsync(prefix + Environment.NewLine + script);
        Assert.Equal(expected, result.Success);
        if (expected) Assert.Contains("ADAPANEL-MIGRATION-CREATED=AdaPanel-Migration-fixture", result.StandardOutput);
        else Assert.DoesNotContain("ADAPANEL-MIGRATION-CREATED=", result.StandardOutput);
    }
}
