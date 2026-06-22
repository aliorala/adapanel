param()

$features = @(
    "IIS-WebServerRole",
    "IIS-WebServer",
    "IIS-CommonHttpFeatures",
    "IIS-DefaultDocument",
    "IIS-DirectoryBrowsing",
    "IIS-HttpErrors",
    "IIS-StaticContent",
    "IIS-HttpLogging",
    "IIS-RequestFiltering",
    "IIS-ApplicationDevelopment",
    "IIS-ASP",
    "IIS-ISAPIExtensions",
    "IIS-ISAPIFilter",
    "IIS-ASPNET45",
    "IIS-NetFxExtensibility45",
    "IIS-CGI",
    "IIS-FTPServer",
    "IIS-FTPSvc",
    "IIS-FTPExtensibility",
    "IIS-ManagementConsole",
    "IIS-ManagementScriptingTools"
)

foreach ($feature in $features) {
    $featureInfo = Get-WindowsOptionalFeature -Online -FeatureName $feature -ErrorAction Stop
    if ($featureInfo.State -ne "Enabled") {
        Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart -ErrorAction Stop | Out-Null
    }
}

$managementAssembly = Join-Path $env:windir "System32\inetsrv\Microsoft.Web.Administration.dll"
$appCmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
if (-not (Test-Path -LiteralPath $managementAssembly) -or -not (Test-Path -LiteralPath $appCmd)) {
    throw "IIS management tools could not be enabled."
}

Write-Host "IIS web, CGI, FTP and management features are ready."

# Windows DNS Server rolu (yalniz Windows Server'da vardir): kuruldugunda AdaPanel, site
# DNS kayitlarini dogrudan Windows DNS'e yazar; boylece VPS gercek yetkili ns1/ns2 olarak
# yanit verir. Tamamen otomatik olmasi icin kurulumda burada kuruyoruz. Client Windows'ta
# Install-WindowsFeature yoktur; o durumda sessizce atlanir ve kurulum bozulmaz (best-effort).
try {
    if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
        $dnsFeature = Get-WindowsFeature -Name DNS -ErrorAction SilentlyContinue
        if ($dnsFeature -and -not $dnsFeature.Installed) {
            Install-WindowsFeature -Name DNS -IncludeManagementTools -ErrorAction Stop | Out-Null
            Write-Host "Windows DNS Server role installed."
        }
        elseif ($dnsFeature -and $dnsFeature.Installed) {
            Write-Host "Windows DNS Server role already present."
        }
    }
    else {
        Write-Host "Windows DNS Server role unavailable on this Windows edition; skipped."
    }
}
catch {
    Write-Warning "Windows DNS Server role could not be installed automatically: $($_.Exception.Message)"
}

# FSRM (File Server Resource Manager): disk kotasi (klasor kotalari) icin gerekir. Yalniz
# Windows Server'da vardir; kota ozelliginin calismasi icin kurulumda otomatik ekliyoruz.
# Client Windows'ta yoktur -> sessizce atlanir (best-effort).
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
