using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Common;
using System.Globalization;

namespace AdaPanel.Infrastructure.Scheduler;

public sealed partial class SchedulerService
{
    internal static void ValidateMigrationRequest(CreateScheduledTaskRequest request)
    {
        if (!TaskNameRules.IsCreatable(request.Name) || request.TaskType is not ("command" or "php" or "script" or "url")
            || request.ScheduleType is not ("daily" or "hourly" or "minute" or "minutely") || request.Interval is < 1 or > 10080
            || request.ScheduleType == "daily" && request.Interval != 1 || request.TimeLimitHours is < 0 or > 8760
            || !DateTime.TryParseExact(request.TimeOfDay, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new AppException("migration.task_mapping_required", 409, 409);
    }
    internal static string BuildMigrationCreateScript(CreateScheduledTaskRequest request, ScriptPlan? plan, string? logPath, string? registrationMarker = null)
    {
        var (execute, arguments, workingDirectory) = plan is null ? ResolveAction(request) : (plan.Execute, plan.Arguments, plan.WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(logPath)) { arguments = BuildLoggedArguments(execute, arguments, logPath); execute = "cmd.exe"; }
        ValidateMigrationRequest(request);
        var principal = ScriptTaskPlanner.LimitedUserId;
        var level = 0;
        var interval = Math.Max(1, request.Interval);
        var schedule = request.ScheduleType.Trim().ToLowerInvariant();
        var limit = Math.Max(0, request.TimeLimitHours);
        var marker = registrationMarker ?? "AdaPanel-Migration-" + Guid.NewGuid().ToString("N");
        var trigger = schedule == "daily"
            ? $"$trigger = $definition.Triggers.Create(2)\n$trigger.StartBoundary = ([datetime]::ParseExact('{NormalizeTime(request.TimeOfDay)}','HH:mm',[System.Globalization.CultureInfo]::InvariantCulture)).ToString('s')\n$trigger.DaysInterval = 1"
            : $"$trigger = $definition.Triggers.Create(1)\n$trigger.StartBoundary = (Get-Date).AddMinutes(1).ToString('s')\n$trigger.Repetition.Interval = 'PT{interval}{(schedule is "minute" or "minutely" ? "M" : "H")}'\n$trigger.Repetition.Duration = 'P3650D'\n$trigger.Repetition.StopAtDurationEnd = $false";
        // RegisterTaskDefinition TASK_CREATE=2 | TASK_DISABLE=8 cannot update an existing task.
        // No enabled registration followed by Disable-ScheduledTask: there is never a run window.
        return $@"$ErrorActionPreference = 'Stop'
$service = New-Object -ComObject 'Schedule.Service'
$service.Connect()
try {{ $folder = $service.GetFolder('\AdaPanel') }}
catch {{
  if ($_.Exception.HResult -ne -2147024894) {{ throw }}
  try {{ $folder = $service.GetFolder('\').CreateFolder('AdaPanel') }}
  catch {{ $folder = $service.GetFolder('\AdaPanel') }}
}}
$definition = $service.NewTask(0)
$definition.RegistrationInfo.Source = '{marker}'
$definition.RegistrationInfo.Description = 'AdaPanel migration; disabled until reviewed'
$definition.Settings.Enabled = $false
$definition.Settings.DisallowStartIfOnBatteries = $false
$definition.Settings.StopIfGoingOnBatteries = $false
$definition.Settings.StartWhenAvailable = $true
$definition.Settings.MultipleInstances = 2
$definition.Settings.ExecutionTimeLimit = '{(limit > 0 ? "PT" + limit + "H" : "PT0S")}'
$definition.Principal.UserId = '{EscapeSingleQuoted(principal)}'
$definition.Principal.LogonType = 5
$definition.Principal.RunLevel = {level}
$action = $definition.Actions.Create(0)
$action.Path = '{EscapeSingleQuoted(execute)}'
$action.Arguments = '{EscapeSingleQuoted(arguments)}'
$action.WorkingDirectory = '{EscapeSingleQuoted(workingDirectory)}'
{trigger}
$trigger.Enabled = $true
$created = $folder.RegisterTaskDefinition('{EscapeSingleQuoted(request.Name.Trim())}', $definition, 10, '{EscapeSingleQuoted(principal)}', $null, 5, $null)
$actual = $folder.GetTask('{EscapeSingleQuoted(request.Name.Trim())}')
$read = $actual.Definition
if ($read.RegistrationInfo.Source -ne '{marker}' -or $read.Settings.Enabled -ne $false -or [int]$actual.State -ne 1 -or
    $read.Settings.StartWhenAvailable -ne $true -or $read.Settings.DisallowStartIfOnBatteries -ne $false -or
    $read.Settings.StopIfGoingOnBatteries -ne $false -or [int]$read.Settings.MultipleInstances -ne 2 -or
    $read.Actions.Count -ne 1 -or $read.Triggers.Count -ne 1 -or $read.Principal.UserId -ne '{EscapeSingleQuoted(principal)}' -or
    [int]$read.Principal.RunLevel -ne 0 -or [int]$read.Principal.LogonType -ne 5 -or
    $read.Actions.Item(1).Path -cne $action.Path -or $read.Actions.Item(1).Arguments -cne $action.Arguments -or
    $read.Actions.Item(1).WorkingDirectory -cne $action.WorkingDirectory -or
    [int]$read.Triggers.Item(1).Type -ne [int]$trigger.Type -or $read.Triggers.Item(1).StartBoundary -ne $trigger.StartBoundary -or
    $read.Triggers.Item(1).Enabled -ne $true -or
    [System.Xml.XmlConvert]::ToTimeSpan($read.Settings.ExecutionTimeLimit) -ne [System.Xml.XmlConvert]::ToTimeSpan($definition.Settings.ExecutionTimeLimit)) {{
  throw 'migration.task_disabled_state_unverified'
}}
if ([int]$trigger.Type -eq 2) {{
  if ($read.Triggers.Item(1).DaysInterval -ne 1) {{ throw 'migration.task_schedule_unverified' }}
}} else {{
  if ([System.Xml.XmlConvert]::ToTimeSpan($read.Triggers.Item(1).Repetition.Interval) -ne [System.Xml.XmlConvert]::ToTimeSpan($trigger.Repetition.Interval) -or
      [System.Xml.XmlConvert]::ToTimeSpan($read.Triggers.Item(1).Repetition.Duration) -ne [System.Xml.XmlConvert]::ToTimeSpan($trigger.Repetition.Duration) -or
      $read.Triggers.Item(1).Repetition.StopAtDurationEnd -ne $false) {{ throw 'migration.task_schedule_unverified' }}
}}
[Console]::Out.WriteLine('ADAPANEL-MIGRATION-CREATED={marker}')";
    }
}
