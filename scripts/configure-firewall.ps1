param(
    [int]$Port = 8890,

    # Veri koku (posta sunucusunun kurulu olup olmadigini BURADAN olceriz; tahmin etmeyiz).
    [string]$DataRoot = "$env:ProgramData\AdaPanel",

    # Testler icin: yalnizca fonksiyonlari tanimla, HICBIR guvenlik duvari kurali dokunma.
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = "Stop"

# =======================================================================================
# AG YUZEYI KARARLARI (denetim bulgusu)
# =======================================================================================
# ESKI DAVRANIS: bu betik 6 posta portunu (25/143/465/587/993/995) ve DNS 53'u (TCP+UDP)
# KOSULSUZ, her profile aciyordu. Stalwart kurulu olmayan bir sunucuda 6 posta portu, DNS
# rolu olmayan bir sunucuda da 53 bosuna aciktı; daha kotusu DNS rolu kuruluysa Windows
# varsayilani OZYINELEME ACIK oldugu icin sunucu ilk gunden acik ozyinelemeli cozucu
# (DNS amplifikasyon reflektoru) oluyordu.
#
# YENI DAVRANIS: bir port yalnizca onu DINLEYEN bir bilesen GERCEKTEN kuruluysa acilir.
# Kurulu degilse varsa eski kural KALDIRILIR (idempotent temizlik). Ozyinelemenin kendisi
# enable-iis-features.ps1'de kapatilir (guvenlik duvari kurali ozyinelemeyi engellemez).

<#
.SYNOPSIS
    Posta sunucusu (Stalwart) bu makinede GERCEKTEN kurulu mu?
.DESCRIPTION
    Olcum, panelin kendi kurulum duzenine dayanir (ComponentBootstrapper):
      - bilesen dizini: <DataRoot>\runtimes\stalwart altinda stalwart.exe
      - servislesme:    "AdaStalwart" adli zamanlanmis gorev
    Ikisinden biri yeterlidir. Dosyalar duruyor ama gorev yoksa da posta portlari
    dinlenecek demektir; tersi (gorev var, dosya baska yerde) de gecerlidir.
#>
function Test-AdaPanelMailServerInstalled {
    param([Parameter(Mandatory = $true)][string]$DataRoot)

    $stalwartRoot = Join-Path $DataRoot "runtimes\stalwart"
    if (Test-Path -LiteralPath $stalwartRoot) {
        $exe = Get-ChildItem -LiteralPath $stalwartRoot -Filter "stalwart.exe" -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($exe) { return $true }
    }

    try {
        $task = Get-ScheduledTask -TaskName "AdaStalwart" -ErrorAction Stop
        if ($task) { return $true }
    }
    catch {
        # Gorev yok ya da Get-ScheduledTask bu surumde yok -> dosya olcumune guveniriz.
    }

    return $false
}

<#
.SYNOPSIS
    Windows DNS Server rolu bu makinede GERCEKTEN kurulu mu?
.DESCRIPTION
    53'u yalnizca dinleyen varsa acariz. Olcum servis kaydindan yapilir ("DNS" servisi rolle
    birlikte gelir); Get-Service yoksa/rol yoksa $false doner.
#>
function Test-AdaPanelDnsServerInstalled {
    try {
        $service = Get-Service -Name "DNS" -ErrorAction Stop
        return $null -ne $service
    }
    catch {
        return $false
    }
}

<#
.SYNOPSIS
    Idempotent gelen (inbound) Allow kurali: once sil, sonra ekle.
#>
function Set-AdaPanelFirewallRule {
    param(
        [Parameter(Mandatory = $true)][string]$DisplayName,
        [Parameter(Mandatory = $true)][string]$Protocol,
        [Parameter(Mandatory = $true)][int]$LocalPort
    )

    Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
    New-NetFirewallRule -DisplayName $DisplayName -Direction Inbound -Action Allow -Protocol $Protocol -LocalPort $LocalPort -Profile Any -ErrorAction Stop | Out-Null
}

<#
.SYNOPSIS
    Dinleyicisi olmayan bir portun eski kuralini kaldirir (varsa).
#>
function Remove-AdaPanelFirewallRule {
    param([Parameter(Mandatory = $true)][string]$DisplayName)

    Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
}

if ($DefineFunctionsOnly) {
    # Testler yalnizca fonksiyonlari ister; ana akis (gercek guvenlik duvari yazimi) calismaz.
    return
}

# ---------------------------------------------------------------------------------------
# Panel portu: her zaman acilir (panelin kendisi bu portu dinler).
# ---------------------------------------------------------------------------------------
$ruleName = "AdaPanel Panel $Port"
Set-AdaPanelFirewallRule -DisplayName $ruleName -Protocol TCP -LocalPort $Port
Write-Host "Firewall rule '$ruleName' created."

$resolvedDataRoot = [Environment]::ExpandEnvironmentVariables($DataRoot)

# ---------------------------------------------------------------------------------------
# Mail sunucusu portlari: 25=SMTP/MX (gelen + sunucular arasi), 143=IMAP, 587=Submission,
# 465=SMTPS, 993=IMAPS, 995=POP3S. YALNIZ Stalwart kuruluysa acilir; degilse eski kurallar
# temizlenir. (Stalwart panelden kurulunca ComponentBootstrapper ayni portlari kendisi acar.)
# ---------------------------------------------------------------------------------------
$mailPorts = 25, 143, 465, 587, 993, 995
$mailInstalled = Test-AdaPanelMailServerInstalled -DataRoot $resolvedDataRoot
foreach ($mailPort in $mailPorts) {
    $mailRule = "AdaPanel Mail $mailPort"
    try {
        if ($mailInstalled) {
            Set-AdaPanelFirewallRule -DisplayName $mailRule -Protocol TCP -LocalPort $mailPort
            Write-Host "Firewall rule '$mailRule' created."
        }
        else {
            Remove-AdaPanelFirewallRule -DisplayName $mailRule
            Write-Host "Mail server not installed; firewall rule '$mailRule' skipped."
        }
    } catch {
        Write-Warning "Mail firewall rule for port $mailPort could not be processed: $_"
    }
}

# ---------------------------------------------------------------------------------------
# Web (site) portlari: 80=HTTP, 443=HTTPS. IIS'in varsayilan kurali cogu zaman 443'u (HTTPS) ACMAZ;
# acik degilse barindirilan sitelere HTTPS ile (ve Let's Encrypt HTTP-01 dogrulamasi 80 ile) erisilemez.
# Web portlari KOSULSUZ acilir: panelin varlik sebebi site barindirmaktir.
# ---------------------------------------------------------------------------------------
foreach ($webPort in 80, 443) {
    $webRule = "AdaPanel Web $webPort"
    try {
        Set-AdaPanelFirewallRule -DisplayName $webRule -Protocol TCP -LocalPort $webPort
        Write-Host "Firewall rule '$webRule' created."
    } catch {
        Write-Warning "Web firewall rule for port $webPort could not be created: $_"
    }
}

# ---------------------------------------------------------------------------------------
# DNS sunucusu (Windows DNS rolu): otoriter ad sunucusu hem UDP hem TCP 53 dinler (TCP = zone
# transfer + 512 byte ustu yanitlar). YALNIZ rol kuruluysa acilir; degilse eski kural temizlenir.
# NOT: Rol kuruluysa ozyineleme enable-iis-features.ps1 tarafindan KAPATILIR (Y10).
# ---------------------------------------------------------------------------------------
$dnsInstalled = Test-AdaPanelDnsServerInstalled
foreach ($dnsProto in 'TCP', 'UDP') {
    $dnsRule = "AdaPanel DNS 53 $dnsProto"
    try {
        if ($dnsInstalled) {
            Set-AdaPanelFirewallRule -DisplayName $dnsRule -Protocol $dnsProto -LocalPort 53
            Write-Host "Firewall rule '$dnsRule' created."
        }
        else {
            Remove-AdaPanelFirewallRule -DisplayName $dnsRule
            Write-Host "DNS Server role not installed; firewall rule '$dnsRule' skipped."
        }
    } catch {
        Write-Warning "DNS firewall rule ($dnsProto 53) could not be processed: $_"
    }
}
