param(
    [string]$InstallDir = "C:\Program Files\AdaPanel",
    [string]$DataRoot = "$env:ProgramData\AdaPanel",
    # "1" = verileri KORU (varsayilan guvenli mod): yalnizca panel servisi + self-update gorevi kaldirilir;
    # musteri veritabanlari, IIS siteleri, yedekler, DB servisleri CALISMAYA DEVAM eder (Plesk davranisi).
    # "0" = tam temizlik (asagidaki eski davranis, kullanici kaldirirken ACIKCA onaylarsa).
    [string]$KeepData = "1"
)

$ErrorActionPreference = "SilentlyContinue"

if ($KeepData -eq "1") {
    $panelService = Get-Service -Name "AdaPanel" -ErrorAction SilentlyContinue
    if ($panelService) {
        Stop-Service -Name "AdaPanel" -Force -ErrorAction SilentlyContinue
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Milliseconds 500
            $panelService = Get-Service -Name "AdaPanel" -ErrorAction SilentlyContinue
            if (-not $panelService -or $panelService.Status -eq "Stopped") { break }
        }
        & sc.exe delete "AdaPanel" | Out-Null
    }

    Stop-ScheduledTask -TaskName "AdaPanel-SelfUpdate" -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName "AdaPanel-SelfUpdate" -Confirm:$false -ErrorAction SilentlyContinue

    Write-Host "AdaPanel removed. Hosted sites, databases, and data were preserved."
    exit 0
}

$serviceNames = @(
    "AdaPanel",
    "AdaMariaDB",
    "AdaMySQL",
    "AdaPostgreSQL",
    "AdaRedis",
    "AdaMemcached",
    "docker"
)

foreach ($serviceName in $serviceNames) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service) {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Milliseconds 500
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if (-not $service -or $service.Status -eq "Stopped") {
                break
            }
        }

        & sc.exe delete $serviceName | Out-Null
    }
}

$taskNames = @(
    "AdaPanel Nginx",
    "AdaStalwart",
    "AdaMemcached",
    "AdaPanel-SelfUpdate"
)
foreach ($taskName in $taskNames) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
}

Get-ScheduledTask -TaskPath "\AdaPanel\" -ErrorAction SilentlyContinue |
    ForEach-Object {
        Stop-ScheduledTask -TaskPath $_.TaskPath -TaskName $_.TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskPath $_.TaskPath -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }

$winAcmeRoot = Join-Path $DataRoot "tools\win-acme"
if (Test-Path -LiteralPath $winAcmeRoot) {
    Get-ScheduledTask -ErrorAction SilentlyContinue |
        Where-Object { $_.TaskName -like "win-acme*" -or $_.TaskPath -like "*win-acme*" } |
        ForEach-Object {
            Stop-ScheduledTask -TaskPath $_.TaskPath -TaskName $_.TaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskPath $_.TaskPath -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
        }
}

# AdaPanel'in OLUSTURDUGU tum IIS sitelerini (kullanici domainleri dahil) kaldir. AdaPanel
# siteleri her zaman 'AdaPanel.*' app pool'u kullanir; bu kesin bir isarettir. Boylece panel
# kaldirilinca Windows'ta artik site/domain kaydi kalmaz (Plesk gibi temiz kaldirma).
try {
    Import-Module WebAdministration -ErrorAction Stop
    Get-Website -ErrorAction SilentlyContinue |
        Where-Object { $_.applicationPool -like 'AdaPanel.*' } |
        ForEach-Object {
            Stop-Website -Name $_.Name -ErrorAction SilentlyContinue
            Remove-Website -Name $_.Name -ErrorAction SilentlyContinue
        }
    Get-ChildItem IIS:\AppPools -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'AdaPanel.*' } |
        ForEach-Object { Remove-WebAppPool -Name $_.Name -ErrorAction SilentlyContinue }
}
catch {
    # WebAdministration modulu yoksa asagidaki appcmd yedegi en azindan yardimci siteleri kaldirir.
}

$appCmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
if (Test-Path -LiteralPath $appCmd) {
    # Yedek: app pool'u 'AdaPanel.*' olan siteleri appcmd ile de kaldir (WebAdministration yoksa).
    $managedSites = & $appCmd list site /text:name 2>$null
    foreach ($siteName in $managedSites) {
        if ([string]::IsNullOrWhiteSpace($siteName)) { continue }
        $pool = & $appCmd list site "$siteName" /text:[path='/'].applicationPool 2>$null
        if ($pool -like "AdaPanel.*") {
            & $appCmd stop site /site.name:"$siteName" 2>$null | Out-Null
            & $appCmd delete site /site.name:"$siteName" 2>$null | Out-Null
        }
    }

    foreach ($siteName in @("AdaPanel.WebDb.PhpMyAdmin", "AdaPanel.WebDb.Adminer")) {
        & $appCmd stop site /site.name:"$siteName" 2>$null | Out-Null
        & $appCmd delete site "$siteName" 2>$null | Out-Null
        & $appCmd delete apppool "AdaPanel.$siteName" 2>$null | Out-Null
    }

    # AdaPanel.* app pool'larini kaldir.
    $managedPools = & $appCmd list apppool /text:name 2>$null
    foreach ($poolName in $managedPools) {
        if ($poolName -like "AdaPanel.*") {
            & $appCmd delete apppool /apppool.name:"$poolName" 2>$null | Out-Null
        }
    }
}

function Invoke-ManagedUninstaller {
    param(
        [string]$Executable,
        [string]$Arguments
    )

    if (-not (Test-Path -LiteralPath $Executable)) {
        return
    }

    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru -ErrorAction SilentlyContinue
    if ($process -and $process.ExitCode -notin @(0, 1605, 1641, 3010)) {
        Write-Warning "Uninstaller failed: $Executable (exit $($process.ExitCode))"
    }
}

$downloadsRoot = Join-Path $DataRoot "downloads"
$postgresRoot = Join-Path $DataRoot "runtimes\postgresql\16"
$postgresUninstaller = Get-ChildItem -LiteralPath $postgresRoot -Filter "uninstall-postgresql*.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($postgresUninstaller) {
    Invoke-ManagedUninstaller $postgresUninstaller.FullName "--mode unattended"
}

$pgAdminRoot = Join-Path $DataRoot "tools\pgadmin4"
$pgAdminUninstaller = Get-ChildItem -LiteralPath $pgAdminRoot -Filter "unins*.exe" -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    Select-Object -First 1
if ($pgAdminUninstaller) {
    Invoke-ManagedUninstaller $pgAdminUninstaller.FullName "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
}

$msiCleanup = @(
    @{ Marker = "runtimes\nodejs"; Pattern = "node-*-x64.msi" },
    @{ Marker = "components\iis-url-rewrite"; Pattern = "rewrite_*.msi" },
    @{ Marker = "components\iis-arr"; Pattern = "requestRouter_*.msi" },
    @{ Marker = "components\http-platform-handler"; Pattern = "httpPlatformHandler_*.msi" }
)
foreach ($item in $msiCleanup) {
    if (-not (Test-Path -LiteralPath (Join-Path $DataRoot $item.Marker))) {
        continue
    }

    $package = Get-ChildItem -LiteralPath $downloadsRoot -Filter $item.Pattern -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($package) {
        Invoke-ManagedUninstaller "msiexec.exe" "/x `"$($package.FullName)`" /qn /norestart"
    }
}

if (Test-Path -LiteralPath (Join-Path $DataRoot "runtimes\python\3")) {
    $pythonInstaller = Get-ChildItem -LiteralPath $downloadsRoot -Filter "python-*-amd64.exe" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($pythonInstaller) {
        Invoke-ManagedUninstaller $pythonInstaller.FullName "/uninstall /quiet"
    }
}

$nginxRoot = Join-Path $DataRoot "runtimes\nginx"
$resolvedNginxRoot = [System.IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($nginxRoot)
).TrimEnd('\') + '\'

Get-Process -Name "nginx" -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $processPath = $_.Path
        if (-not [string]::IsNullOrWhiteSpace($processPath)) {
            $resolvedProcessPath = [System.IO.Path]::GetFullPath($processPath)
            if ($resolvedProcessPath.StartsWith($resolvedNginxRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
    }
}

$resolvedDataRoot = [System.IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($DataRoot)
).TrimEnd('\') + '\'
Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $processPath = $_.Path
        if (-not [string]::IsNullOrWhiteSpace($processPath)) {
            $resolvedProcessPath = [System.IO.Path]::GetFullPath($processPath)
            if ($resolvedProcessPath.StartsWith($resolvedDataRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
    }
}

Get-NetFirewallRule -DisplayName "AdaPanel*" -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue |
    Out-Null

foreach ($certificatePath in @("Cert:\LocalMachine\My", "Cert:\LocalMachine\Root", "Cert:\CurrentUser\My", "Cert:\CurrentUser\Root")) {
    Get-ChildItem $certificatePath -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -eq "AdaPanel Self Signed" } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

# KLASOR BAGLANTILARI (junction) SILMEDEN ONCE KALDIRILIR.
#
# NEDEN: veri kokunun altinda FTP kullanicilari icin junction'lar durur
# (ftp-root\LocalUser\<kullanici>) ve bunlarin HEDEFI panelin disindadir -- musterinin SITE
# KOKUDUR. Windows PowerShell 5.1'in bilinen reparse-point hatasinda "Remove-Item -Recurse"
# baglantiyi TAKIP EDER; yani panelin kaldirilmasi musterilerin butun site dosyalarini
# silebilir. Yedekten donmeden geri alinamayan bir kayiptir.
#
# Asagidaki dolasim baglantilarin ICINE GIRMEZ: bir klasor reparse point ise yalniz baglanti
# kaldirilir (IO.Directory::Delete hedefi takip etmez), degilse alt klasorlerine inilir.
function Remove-AdaReparsePoints {
    param([Parameter(Mandatory = $true)][string] $Root)

    if (-not [System.IO.Directory]::Exists($Root)) {
        return
    }

    $bekleyen = New-Object 'System.Collections.Generic.Stack[string]'
    $bekleyen.Push($Root)

    while ($bekleyen.Count -gt 0) {
        $simdiki = $bekleyen.Pop()

        try {
            $altKlasorler = [System.IO.Directory]::EnumerateDirectories($simdiki)
        } catch {
            continue
        }

        foreach ($alt in $altKlasorler) {
            try {
                $nitelikler = [System.IO.File]::GetAttributes($alt)
            } catch {
                continue
            }

            if ($nitelikler -band [System.IO.FileAttributes]::ReparsePoint) {
                # Yalnizca baglantiyi kaldirir; hedefteki dosyalara DOKUNMAZ.
                try { [System.IO.Directory]::Delete($alt) } catch { }
            } else {
                $bekleyen.Push($alt)
            }
        }
    }
}

foreach ($path in @($DataRoot, $InstallDir)) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        continue
    }

    $expandedPath = [Environment]::ExpandEnvironmentVariables($path)
    if (Test-Path -LiteralPath $expandedPath) {
        Remove-AdaReparsePoints -Root $expandedPath
        Remove-Item -LiteralPath $expandedPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "AdaPanel uninstall cleanup completed."
