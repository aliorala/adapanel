param(
    [ValidateSet('Stop', 'Restore')][string]$Action = 'Stop',
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ServiceName = 'AdaPanel',
    [string]$StatePath,
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'

function Restore-AdaUpdateService([string]$Name, [string]$Snapshot) {
    if (-not (Test-Path -LiteralPath $Snapshot)) { return }
    $saved = Get-Content -LiteralPath $Snapshot -Raw | ConvertFrom-Json
    if ($saved.Name -ne $Name -or $saved.Start -notin @(2, 3, 4)) {
        throw 'Invalid update service snapshot.'
    }
    $mode = @{ 2 = 'Automatic'; 3 = 'Manual'; 4 = 'Disabled' }[[int]$saved.Start]
    Set-Service -Name $Name -StartupType $mode -ErrorAction Stop
    if ($null -ne $saved.DelayedAutoStart) {
        Set-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -Name DelayedAutoStart -Value ([int]$saved.DelayedAutoStart) -ErrorAction Stop
    }
    Write-Output "Service startup restored: $mode."
}

function Stop-AdaUpdateService([string]$Name, [string]$Snapshot) {
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    $config = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -ErrorAction Stop
    if ($config.ImagePath -match '^\s*"([^"]+)"') { $exe = $Matches[1] }
    elseif ($config.ImagePath -match '^\s*(\S+)') { $exe = $Matches[1] }
    else { throw 'Cannot resolve panel service executable.' }
    $exe = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($exe))
    if ([IO.Path]::GetFileName($exe) -ne 'AdaPanel.Api.exe') { throw 'Unexpected panel service executable.' }

    # Persist BEFORE disabling. The second stop (PrepareToInstall) must not save Disabled
    # over the original setting captured by InitializeSetup.
    if (-not (Test-Path -LiteralPath $Snapshot)) {
        if ($config.Start -notin @(2, 3, 4)) { throw 'Unsupported service startup type.' }
        @{ Name = $Name; Start = [int]$config.Start; DelayedAutoStart = $config.DelayedAutoStart } |
            ConvertTo-Json | Set-Content -LiteralPath $Snapshot -Encoding UTF8 -ErrorAction Stop
    }

    # SCM queued recovery cannot be cancelled by stopping again or clearing failure actions.
    # Disabled prevents the pending restart until the installer has finished replacing files.
    Set-Service -Name $Name -StartupType Disabled -ErrorAction Stop
    try {
        $svc.Refresh()
        if ($svc.Status -ne 'Stopped') {
            if ($svc.Status -ne 'StopPending') { $svc.Stop() }
            $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        }
    } catch { Write-Output 'Graceful stop incomplete; checking the exact panel executable.' }

    # Do not taskkill /IM: another installation may use the same process name.
    foreach ($process in @(Get-Process -Name 'AdaPanel.Api' -ErrorAction SilentlyContinue)) {
        if (-not $process.Path) { throw 'Cannot verify panel process path.' }
        if ([IO.Path]::GetFullPath($process.Path) -eq $exe) {
            Stop-Process -InputObject $process -Force -ErrorAction Stop
            if (-not $process.WaitForExit(10000)) { throw 'Panel process did not exit.' }
        }
    }
    $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(10))
    $svc.Refresh()
    if ($svc.Status -ne 'Stopped') { throw 'Panel service is still running.' }
    # Fail before wwwroot is moved or binaries are overwritten if a file is still locked.
    if (Test-Path -LiteralPath $exe) {
        $handle = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $handle.Dispose()
    }
    Write-Output 'Panel stopped; service restart blocked during file replacement.'
}

if ($DefineFunctionsOnly) { return }
if ([string]::IsNullOrWhiteSpace($StatePath)) { throw 'StatePath is required.' }
if ($Action -eq 'Restore') { Restore-AdaUpdateService $ServiceName $StatePath }
else { Stop-AdaUpdateService $ServiceName $StatePath }
