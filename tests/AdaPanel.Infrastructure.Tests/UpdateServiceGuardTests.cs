using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class UpdateServiceGuardTests
{
    [Theory]
    [InlineData(2, "Automatic")]
    [InlineData(3, "Manual")]
    [InlineData(4, "Disabled")]
    public void Restart_is_blocked_until_original_startup_is_restored(int start, string expected)
    {
        Run($$"""
            $script:start = {{start}}
            Stop-AdaUpdateService 'AdaPanel' $snapshot
            if ($script:mode -ne 'Disabled') { throw 'Restart not blocked' }
            # Simulate PrepareToInstall after InitializeSetup: original state must survive.
            $script:start = 4
            Stop-AdaUpdateService 'AdaPanel' $snapshot
            Restore-AdaUpdateService 'AdaPanel' $snapshot
            if ($script:mode -ne '{{expected}}') { throw 'Original startup lost' }
            if ($script:delayed -ne 1) { throw 'Delayed startup lost' }
            if ($script:otherKilled) { throw 'Other installation killed' }
            """);
    }

    [Fact]
    public void Failed_disable_aborts_before_stopping_or_killing()
    {
        Run("""
            $script:denyDisable = $true
            try { Stop-AdaUpdateService 'AdaPanel' $snapshot; throw 'Expected failure' }
            catch { if ($_.Exception.Message -ne 'Denied') { throw } }
            if ($script:stopped -or $script:killed) { throw 'Continued after disable failure' }
            Restore-AdaUpdateService 'AdaPanel' $snapshot
            if ($script:mode -ne 'Automatic') { throw 'Restore failed' }
            """);
    }

    [Fact]
    public void Forced_stop_cannot_trigger_recovery_and_does_not_touch_other_installation()
    {
        Run("""
            $script:gracefulFails = $true
            Stop-AdaUpdateService 'AdaPanel' $snapshot
            if (-not $script:killed) { throw 'Exact process not stopped' }
            if ($script:otherKilled) { throw 'Wrong process stopped' }
            if ($script:mode -ne 'Disabled') { throw 'Recovery could restart during copying' }
            Restore-AdaUpdateService 'AdaPanel' $snapshot
            if ($script:mode -ne 'Automatic') { throw 'Cancellation did not restore startup' }
            """);
    }

    [Fact]
    public void No_service_is_a_noop()
    {
        Run("""
            function Get-Service { return $null }
            Stop-AdaUpdateService 'AdaPanel' $snapshot
            Restore-AdaUpdateService 'AdaPanel' $snapshot
            if (Test-Path -LiteralPath $snapshot) { throw 'Snapshot created for absent service' }
            """);
    }

    private static void Run(string scenario)
    {
        var script = ScriptTestHost.PowerShellLiteral(ScriptTestHost.ScriptPath("scripts", "update-service-guard.ps1"));
        var result = ScriptTestHost.Run($$"""
            . {{script}} -DefineFunctionsOnly
            $snapshot = Join-Path $env:TEMP ('ada-guard-' + [guid]::NewGuid().ToString('N') + '.json')
            $script:start = 2
            $script:mode = 'Automatic'
            $script:svc = [pscustomobject]@{ Status = 'Running' }
            $script:svc | Add-Member ScriptMethod Refresh { }
            $script:svc | Add-Member ScriptMethod Stop {
                if ($script:mode -ne 'Disabled') { throw 'Stopped before disabling' }
                if ($script:gracefulFails) { throw 'Stop hung' }
                $script:stopped = $true; $this.Status = 'Stopped'
            }
            $script:svc | Add-Member ScriptMethod WaitForStatus {
                param($status, $timeout)
                if ($this.Status -ne 'Stopped') { throw 'Still running' }
            }
            function Get-Service { return $script:svc }
            function Get-ItemProperty {
                return [pscustomobject]@{ Start = $script:start; DelayedAutoStart = 1; ImagePath = '"C:\guard-fixture\AdaPanel.Api.exe" --urls https://localhost:8890' }
            }
            function Set-Service {
                param($Name, $StartupType, $ErrorAction)
                if ($script:denyDisable -and $StartupType -eq 'Disabled') { throw 'Denied' }
                $script:mode = $StartupType
            }
            function Set-ItemProperty { param($LiteralPath,$Name,$Value,$ErrorAction); $script:delayed = $Value }
            function Get-Process {
                $own = [pscustomobject]@{ Path = 'C:\guard-fixture\AdaPanel.Api.exe' }
                $own | Add-Member ScriptMethod WaitForExit { param($ms); return $true }
                return @($own, [pscustomobject]@{ Path = 'C:\another-panel\AdaPanel.Api.exe' })
            }
            function Stop-Process {
                param($InputObject,[switch]$Force,$ErrorAction)
                if ($script:mode -ne 'Disabled') { throw 'SCM restarted the killed service' }
                if ($InputObject.Path -like '*another-panel*') { $script:otherKilled = $true }
                $script:killed = $true; $script:svc.Status = 'Stopped'
            }
            try { {{scenario}}; 'guard-ok' }
            finally { if (Test-Path -LiteralPath $snapshot) { Remove-Item -LiteralPath $snapshot -Force } }
            """);
        Assert.True(result.ExitCode == 0, result.Tumu);
        Assert.Contains("guard-ok", result.StdOut);
    }
}
