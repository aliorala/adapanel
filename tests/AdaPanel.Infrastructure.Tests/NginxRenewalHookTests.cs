using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NginxRenewalHookTests
{
    [Theory]
    [InlineData("default", true)]
    [InlineData("explicit", true)]
    [InlineData("escaped-regex", true)]
    [InlineData("escaped-quoted-value", true)]
    [InlineData("pid-in-argument", true)]
    [InlineData("quoted-directive", true)]
    [InlineData("escaped-pid-name", false)]
    [InlineData("mixed-pid-value", false)]
    [InlineData("duplicate-pid", false)]
    [InlineData("pid-variable", false)]
    [InlineData("pid-extra-value", false)]
    [InlineData("wrong-owner", false)]
    [InlineData("unknown-owner", false)]
    [InlineData("wrong-executable", false)]
    [InlineData("stopped", false)]
    [InlineData("invalid-pid", false)]
    [InlineData("pid-mismatch", false)]
    [InlineData("unsupported-config", false)]
    [InlineData("empty-dump", false)]
    [InlineData("validation-failed", false)]
    [InlineData("reload-failed", false)]
    [InlineData("master-changed", false)]
    public void Hook_checks_identity_effective_pid_and_validation_before_signalling(string scenario, bool succeeds)
    {
        var script = ScriptTestHost.ScriptPath("scripts", "win-acme-nginx-reload.ps1");
        var result = ScriptTestHost.Run($$"""
            . {{ScriptTestHost.PowerShellLiteral(script)}} -DefineFunctionsOnly
            $scenario = '{{scenario}}'
            $root = Join-Path ([IO.Path]::GetTempPath()) ('ada-nginx-hook-' + [guid]::NewGuid().ToString('N'))
            $null = New-Item -ItemType Directory -Path (Join-Path $root 'logs') -Force
            $exe = Join-Path $root 'nginx.exe'
            $config = Join-Path $root 'nginx.conf'
            $pidFile = Join-Path $root 'logs/nginx.pid'
            [IO.File]::WriteAllText($exe, 'fixture')
            [IO.File]::WriteAllText($config, 'fixture')
            [IO.File]::WriteAllText($pidFile, '43210')
            if ($scenario -eq 'invalid-pid') { [IO.File]::WriteAllText($pidFile, 'broken') }
            $script:validated = $false
            function Get-AdaNginxCurrentSid { 'S-1-5-18' }
            function Get-CimInstance {
                if ($scenario -eq 'stopped') { return $null }
                $path = $exe
                if ($scenario -eq 'wrong-executable') { $path = Join-Path $root 'unrelated.exe' }
                $date = [datetime]'2026-01-01Z'
                if ($scenario -eq 'master-changed' -and $script:validated) { $date = $date.AddMinutes(1) }
                [pscustomobject]@{ ExecutablePath = $path; CreationDate = $date }
            }
            function Invoke-CimMethod {
                $sid = 'S-1-5-18'
                if ($scenario -eq 'wrong-owner') { $sid = 'S-1-5-19' }
                if ($scenario -eq 'unknown-owner') { $sid = $null }
                [pscustomobject]@{ ReturnValue = 0; Sid = $sid }
            }
            function Invoke-AdaNginxCommand {
                param($Executable, $WorkingDirectory, $Arguments)
                if ($Arguments.StartsWith('-t -T ')) {
                    Write-Host 'VALIDATED'
                    if ($scenario -eq 'validation-failed') { throw 'fixture validation failed' }
                    $script:validated = $true
                    if ($scenario -eq 'empty-dump') { return '' }
                    $dump = "# configuration file fixture:`nevents {}`nhttp { # pid ignored;`n}"
                    if ($scenario -eq 'explicit') { $dump += "`npid `"logs/nginx.pid`";" }
                    if ($scenario -eq 'escaped-regex') { $dump += "`n" + 'http { server { location ~ \.php$ { return 200 ok; } } }' }
                    if ($scenario -eq 'escaped-quoted-value') { $dump += "`n" + 'http { log_format custom "literal \"pid\" and \\slashes"; }' }
                    if ($scenario -eq 'pid-in-argument') { $dump += "`n" + 'env pid; http { log_format custom "pid fake.pid;"; }' }
                    if ($scenario -eq 'quoted-directive') { $dump += "`n" + '"env" ESCAPED;' }
                    if ($scenario -eq 'escaped-pid-name') { $dump += "`n" + 'p\id logs/other.pid;' }
                    if ($scenario -eq 'mixed-pid-value') { $dump += "`n" + 'pid "logs/"nginx.pid;' }
                    if ($scenario -eq 'duplicate-pid') { $dump += "`npid logs/nginx.pid; pid logs/nginx.pid;" }
                    if ($scenario -eq 'pid-variable') { $dump += "`n" + 'pid $pid;' }
                    if ($scenario -eq 'pid-extra-value') { $dump += "`npid logs/nginx.pid other;" }
                    if ($scenario -eq 'pid-mismatch') { $dump += "`npid logs/other.pid;" }
                    if ($scenario -eq 'unsupported-config') { $dump += "`n" + 'pid "bad\escape.pid";' }
                    return $dump
                }
                if (-not $script:validated -or -not $Arguments.EndsWith(' -s reload')) { throw 'unsafe command order' }
                Write-Host 'SIGNALLED'
                if ($scenario -eq 'reload-failed') { throw 'fixture reload failed' }
            }
            try {
                Invoke-AdaNginxRenewalReload $exe $root $config $pidFile
            }
            finally { Remove-Item -LiteralPath $root -Recurse -Force }
            """);

        Assert.True(succeeds ? result.ExitCode == 0 : result.ExitCode != 0, result.Tumu);
        if (succeeds)
        {
            Assert.Contains("VALIDATED", result.Tumu);
            Assert.Contains("SIGNALLED", result.Tumu);
            Assert.Contains("graceful reload signal completed", result.Tumu);
        }
        else
        {
            Assert.DoesNotContain("graceful reload signal completed", result.Tumu);
            if (scenario != "reload-failed") Assert.DoesNotContain("SIGNALLED", result.Tumu);
        }
    }

    [Fact]
    public void Public_hook_returns_nonzero_for_missing_arguments()
    {
        var script = ScriptTestHost.ScriptPath("scripts", "win-acme-nginx-reload.ps1");
        var result = ScriptTestHost.Run($"& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {ScriptTestHost.PowerShellLiteral(script)}\nexit $LASTEXITCODE");
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("graceful reload signal completed", result.Tumu);
    }
}
