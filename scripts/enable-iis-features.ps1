param(
    # Testler icin: yalnizca fonksiyonlari tanimla, HICBIR Windows ozelligi/rolu kurma.
    [switch]$DefineFunctionsOnly
)

# IIS ozellikleri site BARINDIRMA icindir; panelin kendisi (Kestrel Windows servisi) IIS olmadan da
# calisir. Bu yuzden bu script "best-effort"tur: tek tek ozellik hatalari kurulumu BOZMAZ, yalnizca
# uyari verir ve cikis kodu 0 doner. Boylece panel servisi her durumda kurulup acilir.
$ErrorActionPreference = "Stop"

# =======================================================================================
# Y10 - DNS: OZYINELEMEYI KAPAT (acik cozucu olmayi engelle)
# =======================================================================================
# Windows DNS Server rolu kurulunca OZYINELEME (recursion) VARSAYILAN OLARAK ACIKtir. AdaPanel'in
# DNS'e ihtiyaci YALNIZCA otoriter yayindir (barindirilan alan adlarinin kendi bolgeleri); baskasi
# adina sorgu cozmesi HIC gerekmez. Ozyineleme acik + 53 disariya acik = DNS amplifikasyon
# reflektoru: saglayici kotuye kullanim bildirimi ve IP karaliste riski, ILK GUNDEN.
#
# Bu yuzden rol kurulur kurulmaz (ve zaten kuruluysa da her calistirmada, idempotent) ozyineleme
# KAPATILIR. Otoriter yanitlar etkilenmez; yalnizca "senin bolgen olmayan adi benim icin coz"
# istekleri reddedilir. Cmdlet yoksa SESSIZ GECMEYIZ: elle yapilacak komut basilir (yanlis
# guvence vermek, acigi bilmemekten daha kotudur).
function Disable-AdaPanelDnsRecursion {
    if (-not (Get-Command Set-DnsServerRecursion -ErrorAction SilentlyContinue)) {
        Write-Warning "Set-DnsServerRecursion bulunamadi; DNS ozyinelemesi KAPATILAMADI. Sunucuda elle: Set-DnsServerRecursion -Enable `$false"
        return $false
    }

    try {
        Set-DnsServerRecursion -Enable $false -ErrorAction Stop
        Write-Host "DNS recursion disabled (authoritative-only; open resolver riski kapatildi)."
        return $true
    }
    catch {
        Write-Warning "DNS ozyinelemesi kapatilamadi: $($_.Exception.Message). Sunucuda elle kapatin: Set-DnsServerRecursion -Enable `$false"
        return $false
    }
}

if ($DefineFunctionsOnly) {
    # Testler yalnizca fonksiyonlari ister; rol/ozellik kurulumu HIC calismaz.
    return
}

function Enable-AdaPanelIisFeatures {
    # Windows SERVER: dogru API Install-WindowsFeature (ServerManager). DISM isimleri Server'da
    # tutarsiz davranabilir; bu yuzden Server'da rol/ozellik isimlerini kullaniyoruz.
    if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
        $serverFeatures = @(
            "Web-Server", "Web-WebServer", "Web-Common-Http", "Web-Default-Doc", "Web-Dir-Browsing",
            "Web-Http-Errors", "Web-Static-Content", "Web-Http-Logging", "Web-Filtering",
            "Web-App-Dev", "Web-Asp-Net45", "Web-Net-Ext45", "Web-ISAPI-Ext", "Web-ISAPI-Filter",
            "Web-CGI", "Web-Asp", "Web-Mgmt-Console", "Web-Scripting-Tools",
            "Web-Ftp-Server", "Web-Ftp-Service", "Web-Ftp-Ext"
        )
        foreach ($feature in $serverFeatures) {
            try {
                $info = Get-WindowsFeature -Name $feature -ErrorAction SilentlyContinue
                if ($info -and -not $info.Installed) {
                    Install-WindowsFeature -Name $feature -IncludeManagementTools -ErrorAction Stop | Out-Null
                }
            }
            catch {
                Write-Warning "IIS ozelligi '$feature' eklenemedi: $($_.Exception.Message)"
            }
        }
        return
    }

    # Windows CLIENT (10/11): DISM optional features.
    $clientFeatures = @(
        "IIS-WebServerRole", "IIS-WebServer", "IIS-CommonHttpFeatures", "IIS-DefaultDocument",
        "IIS-DirectoryBrowsing", "IIS-HttpErrors", "IIS-StaticContent", "IIS-HttpLogging",
        "IIS-RequestFiltering", "IIS-ApplicationDevelopment", "IIS-ASPNET45", "IIS-NetFxExtensibility45",
        "IIS-ISAPIExtensions", "IIS-ISAPIFilter", "IIS-CGI", "IIS-ASP", "IIS-FTPServer", "IIS-FTPSvc",
        "IIS-FTPExtensibility", "IIS-ManagementConsole", "IIS-ManagementScriptingTools"
    )
    foreach ($feature in $clientFeatures) {
        try {
            $info = Get-WindowsOptionalFeature -Online -FeatureName $feature -ErrorAction Stop
            if ($info.State -ne "Enabled") {
                Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart -ErrorAction Stop | Out-Null
            }
        }
        catch {
            Write-Warning "IIS ozelligi '$feature' etkinlestirilemedi: $($_.Exception.Message)"
        }
    }
}

try {
    Enable-AdaPanelIisFeatures
}
catch {
    Write-Warning "IIS ozellikleri etkinlestirilirken beklenmeyen hata: $($_.Exception.Message)"
}

$managementAssembly = Join-Path $env:windir "System32\inetsrv\Microsoft.Web.Administration.dll"
$appCmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
if ((Test-Path -LiteralPath $managementAssembly) -and (Test-Path -LiteralPath $appCmd)) {
    Write-Host "IIS web, CGI, FTP and management features are ready."
}
else {
    # IIS tam kurulamadi -> paneli (Kestrel servisi) BLOKLAMA. Panel acilir; site barindirma icin
    # IIS sonra panelden/elle tamamlanabilir. Bu yuzden THROW etmiyoruz, cikis kodu 0 kaliyor.
    Write-Warning "IIS tam etkinlestirilemedi (yonetim araclari bulunamadi). Panel yine de calisir; site barindirma icin IIS'i sonra tamamlayin."
}

# Windows DNS Server rolu (yalniz Windows Server'da vardir): kuruldugunda AdaPanel site DNS
# kayitlarini dogrudan Windows DNS'e yazar (gercek yetkili ns1/ns2). Best-effort.
try {
    if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
        $dnsFeature = Get-WindowsFeature -Name DNS -ErrorAction SilentlyContinue
        if ($dnsFeature -and -not $dnsFeature.Installed) {
            Install-WindowsFeature -Name DNS -IncludeManagementTools -ErrorAction Stop | Out-Null
            Write-Host "Windows DNS Server role installed."
            # Y10: rol kurulur kurulmaz ozyinelemeyi kapat -- "sonra yapariz" penceresi birakmayiz.
            [void](Disable-AdaPanelDnsRecursion)
        }
        elseif ($dnsFeature -and $dnsFeature.Installed) {
            Write-Host "Windows DNS Server role already present."
            # Zaten kuruluysa da her calistirmada uygula (idempotent; eski kurulumlar da duzelir).
            [void](Disable-AdaPanelDnsRecursion)
        }
    }
    else {
        Write-Host "Windows DNS Server role unavailable on this Windows edition; skipped."
    }
}
catch {
    Write-Warning "Windows DNS Server role could not be installed automatically: $($_.Exception.Message)"
}

# FSRM (File Server Resource Manager): disk kotasi icin. Yalniz Windows Server'da vardir. Best-effort.
try {
    if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
        $fsrm = Get-WindowsFeature -Name FS-Resource-Manager -ErrorAction SilentlyContinue
        if ($fsrm -and -not $fsrm.Installed) {
            Install-WindowsFeature -Name FS-Resource-Manager -IncludeManagementTools -ErrorAction Stop | Out-Null
            Write-Host "FSRM (File Server Resource Manager) role installed."
        }
        elseif ($fsrm -and $fsrm.Installed) {
            Write-Host "FSRM role already present."
        }
    }
    else {
        Write-Host "FSRM role unavailable on this Windows edition; skipped."
    }
}
catch {
    Write-Warning "FSRM role could not be installed automatically: $($_.Exception.Message)"
}

# Panel servisi her zaman kurulabilsin diye basariyla cik (best-effort tamam).
exit 0
