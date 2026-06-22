param(
    [string]$ExecutablePath = "C:\Program Files\AdaPanel\AdaPanel.Api.exe",
    [string]$ServiceName = "AdaPanel",
    [string]$Urls = "",
    [int]$PreferredHttpsPort = 0
)

$ErrorActionPreference = "Stop"

$resolvedExecutable = (Resolve-Path -Path $ExecutablePath -ErrorAction Stop).Path
$binaryArguments = @()
if (-not [string]::IsNullOrWhiteSpace($Urls)) {
    $binaryArguments += ('--urls "{0}"' -f $Urls.Trim())
}
if ($PreferredHttpsPort -gt 0) {
    $binaryArguments += ('--Panel:PreferredHttpsPort "{0}"' -f $PreferredHttpsPort)
}
$binaryPath = ('"{0}" {1}' -f $resolvedExecutable, ($binaryArguments -join " ")).Trim()

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null

    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
            break
        }
    }

    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        throw "Service '$ServiceName' could not be removed."
    }
}

New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName $ServiceName -StartupType Automatic -ErrorAction Stop
Start-Service -Name $ServiceName -ErrorAction Stop

Write-Host "Service '$ServiceName' created and started."
