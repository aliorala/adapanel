using AdaPanel.Infrastructure.Health;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class HealthScheduledTaskTests
{
    [Theory]
    [InlineData(4, "Running")]
    [InlineData(2, "StartPending")]
    [InlineData(3, "Stopped")]
    [InlineData(1, "Stopped")]
    [InlineData(0, "Unknown")]
    public async Task Scheduled_component_status_is_read_from_the_task(int state, string expected)
    {
        var result = await WindowsServiceInspection.ReadAsync(["AdaStalwart"], default,
            _ => "NotInstalled", _ => WindowsServiceInspection.TaskStatus(state));
        Assert.Equal(expected, result["AdaStalwart"]);
    }

    [Fact]
    public async Task Existing_service_takes_precedence_over_task_lookup()
    {
        var result = await WindowsServiceInspection.ReadAsync(["AdaMariaDB"], default,
            _ => "Running", _ => throw new Exception("Task lookup should not run"));
        Assert.Equal("Running", result["AdaMariaDB"]);
    }

    [Fact]
    public async Task Unreadable_target_is_unknown_and_does_not_hide_other_services()
    {
        var result = await WindowsServiceInspection.ReadAsync(["denied", "task", "missing", "healthy"], default,
            name => name switch { "denied" => throw new UnauthorizedAccessException(), "healthy" => "Running", _ => "NotInstalled" },
            name => name == "task" ? throw new IOException("Task Scheduler unavailable") : "NotInstalled");
        Assert.Equal("Unknown", result["denied"]);
        Assert.Equal("Unknown", result["task"]);
        Assert.Equal("NotInstalled", result["missing"]);
        Assert.Equal("Running", result["healthy"]);
    }

    [Fact]
    public async Task Canceled_health_query_does_not_read_services()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsServiceInspection.ReadAsync(["test"], source.Token,
            _ => throw new Exception("Must not run")));
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
