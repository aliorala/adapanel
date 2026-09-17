using AdaPanel.Infrastructure.Health;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class HealthScheduledTaskTests
{
    [Theory]
    [InlineData("Running", "Running")]
    [InlineData("Queued", "StartPending")]
    [InlineData("Ready", "Stopped")]
    [InlineData("Disabled", "Stopped")]
    [InlineData("Unknown", "Unknown")]
    public void Scheduled_component_status_is_read_from_the_task(string state, string expected)
    {
        var result = ScriptTestHost.Run($$"""
            function Get-Service { param($Name, $ErrorAction) return $null }
            function Get-ScheduledTask { param($TaskName, $TaskPath, $ErrorAction) [pscustomobject]@{State='{{state}}'} }
            {{HealthService.BuildStatusScript(["AdaStalwart"])}}
            """);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"\"status\":\"{expected}\"", result.StdOut);
    }

    [Fact]
    public void Existing_service_takes_precedence_over_task_lookup()
    {
        var result = ScriptTestHost.Run($$"""
            function Get-Service { param($Name, $ErrorAction) [pscustomobject]@{Status='Running'} }
            function Get-ScheduledTask { throw 'Task lookup should not run' }
            {{HealthService.BuildStatusScript(["AdaMariaDB"])}}
            """);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"status\":\"Running\"", result.StdOut);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restart_uses_the_registered_execution_type(bool service)
    {
        var result = ScriptTestHost.Run($$"""
            function Get-Service { param($Name, $ErrorAction) {{(service ? "[pscustomobject]@{Status='Running'}" : "return $null")}} }
            function Get-ScheduledTask { param($TaskName, $TaskPath, $ErrorAction) [pscustomobject]@{State='Running'} }
            function Restart-Service { param($Name,[switch]$Force,$ErrorAction) Write-Output 'SERVICE-RESTART' }
            function Stop-ScheduledTask { param($InputObject,$ErrorAction) Write-Output 'TASK-STOP' }
            function Start-ScheduledTask { param($InputObject,$ErrorAction) Write-Output 'TASK-START' }
            {{HealthService.BuildRestartScript("AdaStalwart")}}
            """);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(service ? "SERVICE-RESTART" : "TASK-STOP\r\nTASK-START", result.StdOut);
        Assert.DoesNotContain(service ? "TASK-START" : "SERVICE-RESTART", result.StdOut);
    }

    [Fact]
    public void Progress_only_clixml_is_not_reported_as_an_error()
    {
        Assert.Equal("", ProcessRunner.NormalizePowerShellStream("#< CLIXML\n<Objs><Obj S=\"progress\"><S>Preparing modules</S></Obj></Objs>"));
        Assert.Equal("real error", ProcessRunner.NormalizePowerShellStream("#< CLIXML\n<Objs><Obj S=\"progress\"/><S S=\"Error\">real error</S></Objs>"));
    }
}
