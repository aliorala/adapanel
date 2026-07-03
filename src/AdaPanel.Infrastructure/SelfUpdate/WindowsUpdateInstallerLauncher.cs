using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.SelfUpdate;

public sealed class WindowsUpdateInstallerLauncher(JsonFileStore fileStore) : IUpdateInstallerLauncher
{
    private const string TaskName = "AdaPanel-SelfUpdate";
    private const string ServiceName = "AdaPanel";

    public async Task ScheduleAsync(
        string packagePath,
        string packageSha256,
        CancellationToken cancellationToken = default)
    {
        var updatesDirectory = Path.GetFullPath(fileStore.EnsureDirectory("updates"));
        var resolvedPackagePath = Path.GetFullPath(packagePath);
        if (!resolvedPackagePath.StartsWith(
                updatesDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(resolvedPackagePath))
        {
            throw new AppException("update.package_not_ready", 409, 409);
        }

        var launcherPath = Path.Combine(updatesDirectory, "apply-update.ps1");
        var logPath = Path.Combine(updatesDirectory, "apply-update.log");
        await File.WriteAllTextAsync(
            launcherPath,
            BuildLauncherScript(resolvedPackagePath, packageSha256, logPath),
            new UTF8Encoding(false),
            cancellationToken);

        var escapedLauncher = EscapePowerShell(launcherPath);
        var registerScript = $$"""
            $ErrorActionPreference = 'Stop'
            $taskName = '{{TaskName}}'
            if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
              Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
            }
            $arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "{{escapedLauncher}}"'
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(15)
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -StartWhenAvailable
            Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
            """;

        var result = await ProcessRunner.RunPowerShellAsync(registerScript, cancellationToken);
        if (!result.Success)
        {
            throw new AppException(
                "update.install_schedule_failed",
                result.ExitCode,
                500,
                $"StandardOutput:\r\n{result.StandardOutput}\r\nStandardError:\r\n{result.StandardError}");
        }
    }

    internal static string BuildLauncherScript(
        string packagePath,
        string packageSha256,
        string logPath)
    {
        return $$"""
            $ErrorActionPreference = 'Stop'
            $packagePath = '{{EscapePowerShellLiteral(packagePath)}}'
            $expectedHash = '{{EscapePowerShellLiteral(packageSha256)}}'
            $logPath = '{{EscapePowerShellLiteral(logPath)}}'
            $taskName = '{{TaskName}}'
            $serviceName = '{{ServiceName}}'

            function Write-UpdateLog([string]$message) {
              $timestamp = (Get-Date).ToString('o')
              Add-Content -LiteralPath $logPath -Value "$timestamp [runner] $message" -ErrorAction SilentlyContinue
            }

            $failed = $false
            try {
              Start-Sleep -Seconds 5
              $actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256 -ErrorAction Stop).Hash
              if ($actualHash -ne $expectedHash) {
                throw "Update package checksum changed before installation."
              }

              $arguments = @(
                '/VERYSILENT',
                '/SUPPRESSMSGBOXES',
                '/NORESTART',
                '/CLOSEAPPLICATIONS',
                "/LOG=`"$logPath`""
              )
              $process = Start-Process -FilePath $packagePath -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
              Write-UpdateLog "Installer exited with code $($process.ExitCode)."
              if ($process.ExitCode -notin @(0, 1641, 3010)) {
                throw "Installer exited with code $($process.ExitCode)."
              }
            }
            catch {
              $failed = $true
              Write-UpdateLog $_.Exception.ToString()
            }

            # GUVENCE: installer hangi adimda biterse bitsin panel servisi olu birakilamaz.
            # (Kurulum yarim kalirsa/baslama adimi bozulursa buradaki tekrar denemeler paneli ayaga kaldirir.)
            try {
              $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
              if ($null -eq $service) {
                Write-UpdateLog "Service '$serviceName' not found after install; nothing to start."
                $failed = $true
              }
              else {
                for ($attempt = 1; $attempt -le 8; $attempt++) {
                  $service.Refresh()
                  if ($service.Status -eq 'Running') { break }
                  try {
                    Start-Service -Name $serviceName -ErrorAction Stop
                  }
                  catch {
                    Write-UpdateLog "Service start attempt $attempt failed: $($_.Exception.Message)"
                  }
                  Start-Sleep -Seconds 5
                }

                $service.Refresh()
                if ($service.Status -eq 'Running') {
                  Write-UpdateLog "Service '$serviceName' is running."
                }
                else {
                  Write-UpdateLog "Service '$serviceName' is NOT running after retries (status: $($service.Status))."
                  $failed = $true
                }
              }
            }
            catch {
              $failed = $true
              Write-UpdateLog "Service recovery failed: $($_.Exception.ToString())"
            }

            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
            if ($failed) { exit 1 }
            """;
    }

    private static string EscapePowerShell(string value) => value.Replace("\"", "`\"");

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");
}
