using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.SelfUpdate;

public sealed class WindowsUpdateInstallerLauncher(JsonFileStore fileStore) : IUpdateInstallerLauncher
{
    private const string TaskName = "AdaPanel-SelfUpdate";

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
              if ($process.ExitCode -notin @(0, 1641, 3010)) {
                throw "Installer exited with code $($process.ExitCode)."
              }
            }
            catch {
              $timestamp = (Get-Date).ToString('o')
              Add-Content -LiteralPath $logPath -Value "$timestamp $($_.Exception.ToString())"
              exit 1
            }
            finally {
              Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
            }
            """;
    }

    private static string EscapePowerShell(string value) => value.Replace("\"", "`\"");

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");
}
