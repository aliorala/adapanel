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

            # DURUM DOSYASI: panel kurulum boyunca kapali oldugu icin ilerlemeyi kendisi bildiremez.
            # Bunun yerine calistirici her adimda buraya yaziyor; panel geri donunce dosyayi okuyup
            # denemenin BITIP BITMEDIGINI ve sonucunu raporluyor.
            #
            # Cozdugu sorun: kurulum iptal olup saniyeler icinde bittiginde bile arayuz "panel yeniden
            # baslatiliyor" diyerek 15 dakika bekliyordu, cunku YALNIZCA surum degisti mi diye bakiyor,
            # denemenin sonucunu hic sormuyordu. Kullaniciya ne hata ne sonuc gorunuyordu.
            $statePath = Join-Path (Split-Path -Parent $logPath) 'last-attempt.json'
            function Write-UpdateState([string]$phase, [string]$errorText, $exitCode) {
              try {
                $state = [ordered]@{
                  phase = $phase
                  error = $errorText
                  installerExitCode = $exitCode
                  updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
                }
                ($state | ConvertTo-Json -Compress) | Set-Content -LiteralPath $statePath -Encoding UTF8 -ErrorAction SilentlyContinue
              }
              catch { }
            }

            Write-UpdateState 'starting' '' $null

            $failed = $false
            try {
              Start-Sleep -Seconds 5
              $actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256 -ErrorAction Stop).Hash
              if ($actualHash -ne $expectedHash) {
                throw "Update package checksum changed before installation."
              }

              # Servisi KURULUMDAN ONCE durdur. Sebebi: panel bir Windows servisidir ve kurulum
              # yardimcisinin RestartManager'i onu pencere kapatarak durduramaz. Durduramayinca
              # Abort/Retry/Ignore sorar, /SUPPRESSMSGBOXES altinda bu ABORT'a duser ve kurulum
              # cikis kodu 5 ile iptal olur; panel kendi kendini hic guncelleyemez. Canli sunucuda
              # birebir bu yasandi. Asagidaki "guvence" blogu servisi her halukarda geri baslatir.
              try {
                $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                if ($svc -and $svc.Status -ne 'Stopped') {
                  Write-UpdateState 'stopping-service' '' $null
                  Write-UpdateLog "Stopping service '$serviceName' before install."
                  Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
                  for ($i = 0; $i -lt 60; $i++) {
                    Start-Sleep -Milliseconds 500
                    $svc.Refresh()
                    if ($svc.Status -eq 'Stopped') { break }
                  }
                  Write-UpdateLog "Service status before install: $($svc.Status)."
                }
              }
              catch {
                Write-UpdateLog "Could not stop service before install: $($_.Exception.Message)"
              }

              $arguments = @(
                '/VERYSILENT',
                '/SUPPRESSMSGBOXES',
                '/NORESTART',
                "/LOG=`"$logPath`""
              )
              Write-UpdateState 'installing' '' $null
              $process = Start-Process -FilePath $packagePath -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
              Write-UpdateLog "Installer exited with code $($process.ExitCode)."
              Write-UpdateState 'installer-finished' '' $process.ExitCode
              if ($process.ExitCode -notin @(0, 1641, 3010)) {
                throw "Installer exited with code $($process.ExitCode)."
              }
            }
            catch {
              $failed = $true
              Write-UpdateLog $_.Exception.ToString()
              Write-UpdateState 'failed' $_.Exception.Message $null
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

            # SON DURUM: panel geri donunce bunu okuyup kullaniciya sonucu soyleyecek.
            if ($failed) { Write-UpdateState 'failed' 'Kurulum tamamlanamadi.' $null }
            else { Write-UpdateState 'succeeded' '' 0 }

            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
            if ($failed) { exit 1 }
            """;
    }

    private static string EscapePowerShell(string value) => value.Replace("\"", "`\"");

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");
}
