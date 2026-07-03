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

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    # GUNCELLEME YOLU: servisi SILME. 'sc delete' acik bir handle varsa servisi "silinmek uzere
    # isaretli" durumda birakir -> New-Service basarisiz olur -> kurulum servis BASLATILMADAN yarim
    # kalir (panel olu kalir). Yerinde yeniden yapilandirmak (ImagePath guncelle) bu tuzagi tamamen
    # ortadan kaldirir; Plesk/ISPConfig de guncellemede reconfigure eder.
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    for ($wait = 0; $wait -lt 30; $wait++) {
        $existing.Refresh()
        if ($existing.Status -eq "Stopped") { break }
        Start-Sleep -Milliseconds 500
    }

    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name ImagePath -Value $binaryPath
    Set-Service -Name $ServiceName -StartupType Automatic
} else {
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName $ServiceName -StartupType Automatic -ErrorAction Stop | Out-Null
}

# Cokme durumunda Windows servisi kendisi yeniden baslatsin (5sn/10sn/30sn) -> panel olu kalmasin.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# BASLATMA: eski surec portu (8890) veya dosya kilidini hemen birakmamis olabilir -> tekrar dene.
for ($attempt = 1; $attempt -le 6; $attempt++) {
    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        break
    } catch {
        if ($attempt -eq 6) {
            throw "Service '$ServiceName' could not be started after $attempt attempts: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 5
    }
}

Write-Host "Service '$ServiceName' configured and started."
