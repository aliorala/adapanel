<#
.SYNOPSIS
    AdaPanel .htaccess IIS modulunu kurar (GAC + applicationHost.config) ya da KALDIRIR.

.DESCRIPTION
    BU BETIK MODULU ACMAZ. Kurulum bittiginde modul IIS'e KAYITLIDIR ama OLUDUR:
    genel anahtar dosyasi (%ProgramData%\AdaPanel\htaccess\module.switch) OLUSTURULMAZ ve
    o dosya olmadan modul her istegi dokunmadan gecirir.

    Bu, urunun en yuksek etki alanli degisikligi icin bilincli bir varsayilandir: kurulum
    sunucudaki hicbir sitenin davranisini DEGISTIRMEZ. Acmak panelden, site site yapilir.

    ADIMLAR
      1. DLL'i GAC'a kurar (guclu adli olmasi sart).
      2. DLL'in ACL'ini kilitler: kiraci (IIS_IUSRS) OKUYAMAZ bile -- GAC zaten yalniz
         yoneticiye yazilabilir, ama miras bozuksa duzeltilir.
      3. Veri klasorunu ve ACL'ini kurar (kiraci anahtar dosyalarini OKUR, YAZAMAZ).
      4. applicationHost.config'teki <modules> koleksiyonuna girdiyi ekler.

    NEDEN applicationHost.config (site basina web.config DEGIL): bkz.
    src/AdaPanel.Infrastructure/Htaccess/Hosting/HtaccessModuleRegistration.cs icindeki
    RegistrationScopeRationale. Ozet: depoda web.config'e yazan 13 servis var ve HICBIRINDE
    dosya basina kilit YOK; on dorduncu yazar olmak mevcut "son yazan kazanir" hata sinifini
    agirlastirirdi. Global kayitla musterinin web.config'ine HIC dokunulmuyor.

.PARAMETER Remove
    Kaldirma modu: applicationHost.config girdisini siler ve derlemeyi GAC'tan cikarir.
    KURTARMA YOLU: modul sunucuda sorun cikarirsa bu yeterlidir.

.NOTES
    DURUSTLUK: bu betik bu gelistirme makinesinde UCTAN UCA CALISTIRILAMADI -- makinede IIS
    siteleri ve yukseltilmis yetki yok. Yalnizca "IIS yok" ve "yetki yok" dallari olculdu.
    Ilk kurulumda ELLE dogrulanmasi gerekenler betigin sonundaki DOGRULAMA bolumunde.
#>

[CmdletBinding()]
param(
    [string]$ModulePath,
    [string]$DataRoot = (Join-Path $env:ProgramData "AdaPanel"),
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

# --- Kayit kimligi: C# tarafiyla AYNI olmak ZORUNDA -------------------------------------
# Bu dort deger HtaccessModuleRegistration.cs icinde de tanimli ve
# HtaccessHostingRegistrationTests bu dosyayi OKUYUP esitligi dogruluyor. Birini degistirip
# digerini unutmak, "panel kurulu der / IIS baska ad arar" seklinde sessiz bir ariza olurdu.
$ModuleName   = "AdaPanelHtaccessModule"
$TypeName     = "AdaPanel.HtaccessModule.HtaccessHttpModule"
$AssemblyName = "AdaPanel.HtaccessModule"
$PreCondition = "runtimeVersionv4.0"

$appcmd = Join-Path $env:WinDir "system32\inetsrv\appcmd.exe"

function Write-Step($message) { Write-Host "[htaccess] $message" }

function Test-IisPresent {
    return (Test-Path $appcmd)
}

function Get-AssemblyIdentity([string]$path) {
    $name = [System.Reflection.AssemblyName]::GetAssemblyName($path)
    $tokenBytes = $name.GetPublicKeyToken()
    if (-not $tokenBytes -or $tokenBytes.Length -eq 0) {
        # GUCLU AD OLMADAN GAC'A KURULAMAZ. Sessizce devam etmek, kurulumun "basarili" gorunup
        # modulun hicbir zaman yuklenmemesi demek olurdu.
        throw "Modul derlemesi GUCLU ADLI DEGIL: $path. GAC kurulumu imkansiz."
    }
    $token = ($tokenBytes | ForEach-Object { $_.ToString("x2") }) -join ""
    return [pscustomobject]@{
        Version = $name.Version.ToString()
        Token   = $token
        Qualified = "$TypeName, $AssemblyName, Version=$($name.Version), Culture=neutral, PublicKeyToken=$token"
    }
}

function Install-Gac([string]$path) {
    # gacutil.exe SUNUCULARDA YOKTUR (Visual Studio SDK ile gelir). System.EnterpriseServices
    # icindeki Publish.GacInstall her .NET 4 kurulu Windows'ta mevcuttur ve resmi yoldur.
    [System.Reflection.Assembly]::Load("System.EnterpriseServices, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a") | Out-Null
    $publish = New-Object System.EnterpriseServices.Internal.Publish
    $publish.GacInstall($path)
}

function Uninstall-Gac([string]$path) {
    [System.Reflection.Assembly]::Load("System.EnterpriseServices, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a") | Out-Null
    $publish = New-Object System.EnterpriseServices.Internal.Publish
    $publish.GacRemove($path)
}

function Get-RegisteredModule {
    $list = & $appcmd list config -section:system.webServer/modules 2>&1 | Out-String
    return ($list -match [regex]::Escape($ModuleName))
}

function Remove-Registration {
    if (-not (Test-IisPresent)) { return }
    if (Get-RegisteredModule) {
        Write-Step "applicationHost.config girdisi kaldiriliyor."
        & $appcmd set config -section:system.webServer/modules "/-[name='$ModuleName']" /commit:apphost | Out-Null
    } else {
        Write-Step "applicationHost.config girdisi zaten yok."
    }
}

function Set-DataRootAcl([string]$root) {
    $htaccessRoot = Join-Path $root "htaccess"
    $sitesRoot    = Join-Path $htaccessRoot "sites"
    $logRoot      = Join-Path $htaccessRoot "logs"

    New-Item -ItemType Directory -Force -Path $htaccessRoot, $sitesRoot, $logRoot | Out-Null

    $admins = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
    $system = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")
    $iis    = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-568")  # IIS_IUSRS

    # DACL SIFIRDAN kurulur (miras KAPALI, mevcut kurallar KOPYALANMAZ).
    # 0.6.5 turunun olculmus dersi: SetAccessRuleProtection($true, $true) kokte ne varsa
    # (bu makinede Everyone=FullControl) hassas klasore KALICI gomuyordu.
    function Apply([string]$path, [bool]$iisWrite) {
        $acl = New-Object System.Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner($admins)

        $inherit = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit,ObjectInherit"
        $prop    = [System.Security.AccessControl.PropagationFlags]::None
        $allow   = [System.Security.AccessControl.AccessControlType]::Allow

        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins, "FullControl", $inherit, $prop, $allow)))
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system, "FullControl", $inherit, $prop, $allow)))

        # Kiraci ANAHTAR dosyalarini OKUR (yoksa modul sessizce kapali kalir) ama YAZAMAZ
        # (yazabilseydi kendi PHP'siyle SUNUCU GENELINDEKI anahtari cevirebilirdi -- izolasyon acigi).
        $rights = if ($iisWrite) { "ReadAndExecute, Write" } else { "ReadAndExecute" }
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($iis, $rights, $inherit, $prop, $allow)))

        Set-Acl -Path $path -AclObject $acl
    }

    Apply $htaccessRoot $false
    # Gunluk klasoru TEK ISTISNA: modul w3wp icinden yazar. Ayri klasor olmasinin sebebi budur --
    # yazma hakki anahtar dosyalarinin klasorune SIZMASIN.
    Apply $logRoot $true
}

# ============================== KALDIRMA ==============================
if ($Remove) {
    Write-Step "KALDIRMA modu."
    Remove-Registration

    if ($ModulePath -and (Test-Path $ModulePath)) {
        try { Uninstall-Gac $ModulePath; Write-Step "GAC'tan kaldirildi." }
        catch { Write-Warning "GAC'tan kaldirilamadi: $($_.Exception.Message)" }
    }

    # ANAHTAR DOSYALARI SILINMEZ: kaldirma sirasinda veri silmek bu deponun kuralina aykiri
    # (uninstall-cleanup.ps1 -KeepData ile yonetiliyor). Kayit gittigi icin modul zaten yuklenmez.
    Write-Step "Tamamlandi. Modul artik hicbir istege girmiyor."
    exit 0
}

# ============================== KURULUM ==============================
if (-not $ModulePath) {
    $ModulePath = Join-Path $PSScriptRoot "..\$AssemblyName.dll"
}
$ModulePath = [System.IO.Path]::GetFullPath($ModulePath)

if (-not (Test-Path $ModulePath)) {
    # Panel IIS modulu olmadan da tam calisir -> bu adim BEST-EFFORT cagrilir (bkz. AdaPanel.iss).
    Write-Warning "Modul DLL'i bulunamadi: $ModulePath. .htaccess destegi KURULMADI."
    exit 0
}

$identity = Get-AssemblyIdentity $ModulePath
Write-Step "Derleme: $AssemblyName v$($identity.Version) token=$($identity.Token)"

# Veri klasoru IIS'ten BAGIMSIZ kurulur: IIS yoksa bile panel tarafi tutarli kalsin.
Write-Step "Veri klasoru ve erisim izinleri kuruluyor: $DataRoot"
Set-DataRootAcl $DataRoot

if (-not (Test-IisPresent)) {
    Write-Warning "IIS bulunamadi (appcmd.exe yok). GAC ve kayit adimlari ATLANDI."
    exit 0
}

Write-Step "GAC'a kuruluyor."
Install-Gac $ModulePath

# Kaydi ONCE kaldir sonra ekle: tekrar kurulumda "Failed to add duplicate collection element"
# hatasi alinmasin ve on kosul/tip degisikligi GERCEKTEN uygulansin (guncelleme yolu).
Remove-Registration

Write-Step "applicationHost.config'e kaydediliyor (preCondition=$PreCondition)."
& $appcmd set config -section:system.webServer/modules `
    "/+[name='$ModuleName',type='$($identity.Qualified)',preCondition='$PreCondition']" `
    /commit:apphost | Out-Null

if (-not (Get-RegisteredModule)) {
    throw "Kayit dogrulanamadi: '$ModuleName' applicationHost.config'te gorunmuyor."
}

Write-Step "Kayit dogrulandi."
Write-Host ""
Write-Host "  Modul KURULDU ve KAPALI." -ForegroundColor Green
Write-Host "  Hicbir sitenin davranisi degismedi: genel anahtar dosyasi olusturulmadi."
Write-Host "  Acmak icin panel > .htaccess ayarlari (once bir siteyi 'gozlem' modunda deneyin)."
Write-Host ""
Write-Host "  ACIL KAPATMA (panel calismasa bile):" -ForegroundColor Yellow
Write-Host "    del `"$DataRoot\htaccess\module.switch`""
Write-Host "  TAM KALDIRMA:"
Write-Host "    powershell -File `"$PSCommandPath`" -Remove -ModulePath `"$ModulePath`""

# ============================== DOGRULAMA ==============================
# ILK CANLI KURULUMDA ELLE DOGRULANACAKLAR (bu makinede IIS olmadigi icin YAPILAMADI):
#   1. appcmd list config -section:system.webServer/modules  -> girdi gorunuyor mu
#   2. Bir PHP sitesine istek at -> 500 YOK, sayfa normal (modul kapaliyken saydam mi)
#   3. Bir Node/Python sitesine istek at -> 500 YOK (havuz "yonetilen kod yok", on kosul atlamali)
#   4. 32-bit havuzlu bir PHP sitesi -> 500 YOK (bitness on kosulu koymadik)
#   5. module.switch olustur + site anahtari ac -> .htaccess GERCEKTEN uygulaniyor mu
#   6. module.switch sil -> 2 saniye icinde eski davranisa donuyor mu
