using System.Text.Json;
using AdaPanel.Infrastructure.SelfUpdate;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class UpdateLauncherRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ada-launcher-recovery-" + Guid.NewGuid().ToString("N"));
    public UpdateLauncherRecoveryTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_installer_is_not_success_when_binary_version_changes(bool versionChanges)
    {
        var result = Run(5, versionChanges);
        Assert.Equal(1, result.ExitCode);
        var state = State();
        Assert.Equal("failed", state.GetProperty("phase").GetString());
        Assert.Equal(5, state.GetProperty("installerExitCode").GetInt32());
        Assert.Contains("Installer exited with code 5", state.GetProperty("error").GetString());
        Assert.Contains(Trace(), line => line is "controller-start" or "cmdlet-start");
    }

    [Fact]
    public void Missing_service_after_successful_install_is_a_failure()
    {
        var result = Run(0, true, missingService: true);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("failed", State().GetProperty("phase").GetString());
        Assert.Contains("servisi bulunamadi", State().GetProperty("error").GetString());
        Assert.Equal(0, State().GetProperty("installerExitCode").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1641)]
    [InlineData(3010)]
    public void Successful_installer_keeps_actual_exit_code(int code)
    {
        var result = Run(code, true);
        Assert.True(result.ExitCode == 0, result.Tumu);
        Assert.Equal("succeeded", State().GetProperty("phase").GetString());
        Assert.Equal(code, State().GetProperty("installerExitCode").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Service_recovery_timeout_keeps_installer_failure_and_exit_code(int code)
    {
        var result = Run(code, true, stuckService: true);
        Assert.Equal(1, result.ExitCode);
        var error = State().GetProperty("error").GetString();
        if (code != 0) Assert.Contains("Installer exited with code 5", error);
        Assert.Contains("panel servisi baslatilamadi", error);
        Assert.Equal(code, State().GetProperty("installerExitCode").GetInt32());
        Assert.Equal(1, Trace().Count(line => line == "controller-start"));
        Assert.DoesNotContain("cmdlet-start", Trace());
    }

    [Fact]
    public void Paused_service_is_resumed_after_installation()
    {
        var result = Run(0, true, pausedService: true);
        Assert.True(result.ExitCode == 0, result.Tumu);
        Assert.Equal("succeeded", State().GetProperty("phase").GetString());
        Assert.Contains("controller-continue", Trace());
        Assert.DoesNotContain("controller-start", Trace());
    }

    [Fact]
    public void Service_read_error_preserves_both_failures()
    {
        var result = Run(5, true, recoveryThrows: true);
        Assert.Equal(1, result.ExitCode);
        var error = State().GetProperty("error").GetString();
        Assert.Contains("Installer exited with code 5", error);
        Assert.Contains("fixture service read failed", error);
        Assert.Equal(5, State().GetProperty("installerExitCode").GetInt32());
    }

    [Fact]
    public void Pending_service_is_polled_without_duplicate_start_requests()
    {
        var result = Run(0, true, delayedStart: true);
        Assert.True(result.ExitCode == 0, result.Tumu);
        Assert.Equal(1, Trace().Count(line => line == "controller-start"));
        Assert.DoesNotContain("cmdlet-start", Trace());
        Assert.Equal("succeeded", State().GetProperty("phase").GetString());
    }

    [Fact]
    public void Changed_package_is_rejected_before_installer_and_service_stop()
    {
        var result = Run(0, false, badHash: true);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("checksum changed", State().GetProperty("error").GetString());
        Assert.DoesNotContain("installer", Trace());
        Assert.DoesNotContain("stop", Trace());
    }

    private JsonElement State()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "last-attempt.json")));
        return document.RootElement.Clone();
    }

    private string[] Trace() => File.Exists(Path.Combine(root, "trace.txt"))
        ? File.ReadAllLines(Path.Combine(root, "trace.txt")) : [];

    private ScriptTestHost.ScriptResult Run(int code, bool changed, bool missingService = false,
        bool stuckService = false, bool delayedStart = false, bool badHash = false, bool pausedService = false,
        bool recoveryThrows = false)
    {
        var binary = Path.Combine(root, "fixture-api.exe");
        File.WriteAllText(binary, "Not an executable. All native operations are mocked.");
        static string Bool(bool value) => value ? "$true" : "$false";
        var prelude = $$"""
            $script:trace = {{ScriptTestHost.PowerShellLiteral(Path.Combine(root, "trace.txt"))}}
            $script:binary = {{ScriptTestHost.PowerShellLiteral(binary)}}
            $script:installed = $false
            $script:clock = [datetime]'2026-10-20T12:00:00Z'
            $script:changed = {{Bool(changed)}}
            $script:missing = {{Bool(missingService)}}
            $script:stuck = {{Bool(stuckService)}}
            $script:delayed = {{Bool(delayedStart)}}
            $script:paused = {{Bool(pausedService)}}
            $script:recoveryThrows = {{Bool(recoveryThrows)}}
            $script:refreshes = 0
            $script:svc = [pscustomobject]@{ Status = 'Running' }
            $script:svc | Add-Member ScriptMethod Stop {
              Add-Content -LiteralPath $script:trace -Value 'stop'
              $this.Status = 'Stopped'
            }
            $script:svc | Add-Member ScriptMethod Start {
              Add-Content -LiteralPath $script:trace -Value 'controller-start'
              $this.Status = if ($script:stuck -or $script:delayed) { 'StartPending' } else { 'Running' }
            }
            $script:svc | Add-Member ScriptMethod Refresh {
              if ($this.Status -eq 'StartPending' -and -not $script:stuck) {
                $script:refreshes++
                if ($script:refreshes -ge 3) { $this.Status = 'Running' }
              }
            }
            $script:svc | Add-Member ScriptMethod Continue {
              Add-Content -LiteralPath $script:trace -Value 'controller-continue'
              $this.Status = 'Running'
            }
            function Get-Date { return $script:clock }
            function Start-Sleep {
              param($Seconds = 0, $Milliseconds = 0)
              $script:clock = $script:clock.AddSeconds($Seconds).AddMilliseconds($Milliseconds)
            }
            function Get-Service {
              if ($script:installed -and $script:recoveryThrows) { throw 'fixture service read failed' }
              if ($script:installed -and $script:missing) { return $null }
              return $script:svc
            }
            function Start-Service {
              Add-Content -LiteralPath $script:trace -Value 'cmdlet-start'
              $script:svc.Status = if ($script:stuck -or $script:delayed) { 'StartPending' } else { 'Running' }
            }
            function Get-ItemProperty {
              return [pscustomobject]@{ ImagePath = '"' + $script:binary + '"' }
            }
            function Get-Item {
              return [pscustomobject]@{ VersionInfo = [pscustomobject]@{
                ProductVersion = if ($script:installed -and $script:changed) { '2.0.0' } else { '1.0.0' }
              } }
            }
            function Get-FileHash {
              return [pscustomobject]@{ Hash = '{{(badHash ? "WRONG" : "EXPECTED")}}' }
            }
            function Start-Process {
              Add-Content -LiteralPath $script:trace -Value 'installer'
              $script:installed = $true
              if ($script:paused) { $script:svc.Status = 'Paused' }
              return [pscustomobject]@{ ExitCode = {{code}} }
            }
            function Unregister-ScheduledTask {
              Add-Content -LiteralPath $script:trace -Value 'unregister'
            }
            """;
        return ScriptTestHost.Run(prelude + "\n" + WindowsUpdateInstallerLauncher.BuildLauncherScript(
            Path.Combine(root, "fake-setup.exe"), "EXPECTED",
            Path.Combine(root, "apply-update.log"), Path.Combine(root, "update-runner.log")), zamanAsimiMs: 30_000);
    }
}
