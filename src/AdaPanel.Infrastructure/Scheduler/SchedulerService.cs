using System.Globalization;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Scheduler;

public sealed class SchedulerService : ISchedulerService
{
    private const string TaskPath = "\\AdaPanel\\";

    public async Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        var script = $@"$tasks = Get-ScheduledTask -TaskPath '{TaskPath}' -ErrorAction SilentlyContinue | ForEach-Object {{
  $info = Get-ScheduledTaskInfo -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue
  [pscustomobject]@{{
    Name = $_.TaskName
    ScheduleType = if ($_.Triggers.Count -gt 0) {{ $_.Triggers[0].Frequency }} else {{ '' }}
    State = $_.State.ToString()
    Command = if ($_.Actions.Count -gt 0) {{ $_.Actions[0].Execute }} else {{ '' }}
    Arguments = if ($_.Actions.Count -gt 0) {{ $_.Actions[0].Arguments }} else {{ '' }}
    WorkingDirectory = if ($_.Actions.Count -gt 0) {{ $_.Actions[0].WorkingDirectory }} else {{ '' }}
    NextRunTimeUtc = if ($info) {{ [datetime]$info.NextRunTime }} else {{ $null }}
    LastRunTimeUtc = if ($info) {{ [datetime]$info.LastRunTime }} else {{ $null }}
  }}
}}
$tasks | ConvertTo-Json -Depth 5";

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.read_failed", 500, 500);
        }

        return DeserializeArray<ScheduledTaskDto>(result.StandardOutput);
    }

    public async Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var result = await ProcessRunner.RunPowerShellAsync(BuildCreateScript(request), cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.write_failed", 500, 500);
        }

        var tasks = await GetTasksAsync(cancellationToken);
        return tasks.FirstOrDefault(task => string.Equals(task.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? new ScheduledTaskDto
            {
                Name = request.Name.Trim(),
                ScheduleType = request.ScheduleType.Trim(),
                Command = request.Command.Trim(),
                Arguments = request.Arguments?.Trim() ?? string.Empty,
                WorkingDirectory = request.WorkingDirectory?.Trim() ?? string.Empty,
                State = "Ready"
            };
    }

    public async Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }

        var script = $@"Unregister-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(name.Trim())}"" -Confirm:$false -ErrorAction Stop";
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.write_failed", 500, 500);
        }
    }

    private static string BuildCreateScript(CreateScheduledTaskRequest request)
    {
        var scheduleType = request.ScheduleType.Trim().ToLowerInvariant();
        var interval = Math.Max(1, request.Interval);
        var action = $"New-ScheduledTaskAction -Execute \"{EscapePowerShell(request.Command.Trim())}\"";
        if (!string.IsNullOrWhiteSpace(request.Arguments))
        {
            action += $" -Argument \"{EscapePowerShell(request.Arguments.Trim())}\"";
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            action += $" -WorkingDirectory \"{EscapePowerShell(request.WorkingDirectory.Trim())}\"";
        }

        var trigger = scheduleType switch
        {
            "minute" or "minutely" => $"New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes {interval}) -RepetitionDuration ([TimeSpan]::MaxValue)",
            "daily" => $"New-ScheduledTaskTrigger -Daily -At ([datetime]::ParseExact('{NormalizeTime(request.TimeOfDay)}','HH:mm',[System.Globalization.CultureInfo]::InvariantCulture))",
            _ => $"New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Hours {interval}) -RepetitionDuration ([TimeSpan]::MaxValue)"
        };

        return $@"if (Get-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(request.Name.Trim())}"" -ErrorAction SilentlyContinue) {{
  Unregister-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(request.Name.Trim())}"" -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}}
$action = {action}
$trigger = {trigger}
Register-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(request.Name.Trim())}"" -Action $action -Trigger $trigger -RunLevel Highest -Force | Out-Null";
    }

    private static void ValidateRequest(CreateScheduledTaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Command))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }
    }

    private static string NormalizeTime(string timeOfDay)
    {
        return DateTime.TryParseExact(timeOfDay?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "00:00";
    }

    private static string EscapePowerShell(string value) => value.Replace("\"", "`\"");

    private static T[] DeserializeArray<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.Deserialize<T[]>() ?? [],
            JsonValueKind.Object => [document.RootElement.Deserialize<T>()!],
            _ => []
        };
    }
}
