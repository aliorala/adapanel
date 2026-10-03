using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Nginx;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NginxStartupTaskTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-nginx-startup-" + Guid.NewGuid().ToString("N"), "Türkçe test's path");

    public NginxStartupTaskTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "nginx.exe"), "Fixture only; never executed");
    }

    [Theory]
    [InlineData("action")]
    [InlineData("trigger")]
    [InlineData("principal")]
    [InlineData("settings")]
    [InlineData("register")]
    public async Task A_nonterminating_scheduler_error_is_reported_as_registration_failure(string failingStep)
    {
        var error = await Assert.ThrowsAsync<AppException>(() => NginxInstallationSupport.RegisterStartupTaskAsync(root,
            (script, token) => ProcessRunner.RunPowerShellAsync(Stubs(failingStep) + "\n" + script, token), default));
        Assert.Equal("nginx.startup_task_failed", error.MessageKey);
        Assert.Contains("fixture " + failingStep + " failure", error.Details);
    }

    [Fact]
    public async Task Successful_registration_uses_the_intended_executable_and_quoted_arguments()
    {
        CommandResult? captured = null;
        await NginxInstallationSupport.RegisterStartupTaskAsync(root, async (script, token) =>
        {
            captured = await ProcessRunner.RunPowerShellAsync(Stubs("") + "\n" + script + "\n$global:fixtureRegistered | ConvertTo-Json -Compress", token);
            return captured;
        }, default);

        Assert.NotNull(captured);
        using var json = System.Text.Json.JsonDocument.Parse(captured.StandardOutput);
        Assert.Equal(NginxInstallationSupport.StartupTaskName, json.RootElement.GetProperty("TaskName").GetString());
        var action = json.RootElement.GetProperty("Action");
        Assert.Equal(Path.Combine(root, "nginx.exe"), action.GetProperty("Execute").GetString());
        Assert.Equal($"-p \"{Path.GetFullPath(root).Replace('\\', '/')}/\" -c conf/nginx.conf", action.GetProperty("Argument").GetString());
    }

    private static string Stubs(string failingStep) => $$"""
        $ErrorActionPreference = 'Continue'
        $global:fixtureFailingStep = '{{failingStep}}'
        function Invoke-FixtureStep { param($Name)
          if ($global:fixtureFailingStep -eq $Name) { Write-Error ('fixture ' + $Name + ' failure') }
        }
        function New-ScheduledTaskAction { param($Execute,$Argument)
          Invoke-FixtureStep 'action'
          [pscustomobject]@{ Execute=$Execute; Argument=$Argument }
        }
        function New-ScheduledTaskTrigger { param([switch]$AtStartup)
          Invoke-FixtureStep 'trigger'
          [pscustomobject]@{AtStartup=$true}
        }
        function New-ScheduledTaskPrincipal { param($UserId,$LogonType,$RunLevel)
          Invoke-FixtureStep 'principal'
          [pscustomobject]@{UserId=$UserId}
        }
        function New-ScheduledTaskSettingsSet { param([switch]$AllowStartIfOnBatteries,[switch]$DontStopIfGoingOnBatteries,$ExecutionTimeLimit,$RestartCount,$RestartInterval)
          Invoke-FixtureStep 'settings'
          [pscustomobject]@{RestartCount=$RestartCount}
        }
        function Register-ScheduledTask { param($TaskName,$Action,$Trigger,$Principal,$Settings,[switch]$Force)
          Invoke-FixtureStep 'register'
          $global:fixtureRegistered = [pscustomobject]@{TaskName=$TaskName; Action=$Action}
        }
        """;

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
}
