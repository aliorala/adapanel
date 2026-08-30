<#
.SYNOPSIS
    AdaPanel canli (production) Windows VPS guvenlik sertlestirme scripti.

.DESCRIPTION
    Bu script, kurulumu YENIDEN YAPMADAN canli bir AdaPanel sunucusundaki uc kritik
    guvenlik acigini kapatir. Sunucu yoneticisi tarafindan ELLE ve Administrator olarak
    calistirilir. Tum islemler IDEMPOTENT'tir: tekrar tekrar calistirmak guvenlidir.

    Uygulanan duzeltmeler:
      K1      - C:\ProgramData\AdaPanel ACL'lerini kilitle (hassas dosyalari BUILTIN\Users'tan koru);
                DACL SIFIRDAN kurulur, miras kopyalanmaz (K4: eskiden kokteki Everyone=FullControl muhurleniyordu)
      K2-DATA - runtimes icindeki veritabani veri dizinleri + posta sunucusu agacini kiraciya kapat
                (php/nodejs/python DOKUNULMAZ: IIS ve zamanlanmis gorevler RX'e muhtac)
      K3      - Paylasimli PHP php.ini sertlestir (OS komut fonksiyonlarini kapat + surum gizle)
      B2      - Yinelenen (duplicate) Windows Guvenlik Duvari kurallarini temizle
      B6      - Web (80/443) her zaman; DNS (53) YALNIZ rol kuruluysa + ozyineleme KAPATILIR (Y10)
      B7  - phpMyAdmin/Adminer PUBLIC portlarini (8896/8897) KAPAT (artik panel proxy arkasinda)
      B8  - (opt-in -HardenManagementPorts) SMB(445)/RPC(135)/NetBIOS/WinRM(5985) gelen baglantilarini engelle
      RDP - (opt-in -RdpAllowFrom <IP>) RDP(3389) erisimini sadece belirtilen kaynak IP'ye kisitla

    NOT: MariaDB root parolasi BILEREK DEGISTIRILMEZ (sondaki "K2 - NEDEN BURADA YOK"
    bolumune bakin).

.PARAMETER HardenManagementPorts
    Verilirse SMB(445)/RPC(135)/NetBIOS(137-139)/WinRM(5985-5986) gelen baglantilarini ENGELLE.
    Bir hosting sunucusunda bunlar internete acik olmamalidir. WinRM'i uzaktan yonetim icin
    kullaniyorsan bu anahtari VERME.

.PARAMETER RdpAllowFrom
    RDP(3389) erisimini SADECE bu kaynak IP(ler)e kisitla (orn. '203.0.113.5' veya '203.0.113.0/24',
    virgulle birden fazla). BOS birakilirsa RDP'ye DOKUNULMAZ (kilitlenme riski yok), yalniz uyari basilir.

.EXAMPLE
    .\harden-adapanel.ps1
    .\harden-adapanel.ps1 -HardenManagementPorts -RdpAllowFrom '203.0.113.5'

.NOTES
    Hedef: CANLI VPS. Bu makinede DEGIL, sunucuda calistirilmak uzere yazilmistir.
#>

param(
    # SMB(445)/RPC(135)/NetBIOS(137-139)/WinRM(5985-5986) gelen baglantilarini ENGELLE (opt-in).
    [switch]$HardenManagementPorts,

    # RDP(3389) erisimini SADECE bu kaynak IP(ler)e kisitla (virgulle birden fazla). Bos=dokunma.
    [string]$RdpAllowFrom
)

# Idempotent islemlerde tek bir hatanin scripti tamamen durdurmasini istemiyoruz; her
# bolum kendi hatalarini ele alir. Beklenmeyen terminating hatalarda yine de duralim.
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------------------
# 0) Yonetici (elevated) kontrolu — bu adimlar SYSTEM/Administrators yetkisi gerektirir.
# ---------------------------------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "Bu script Administrator (yukseltilmis/elevated) olarak calistirilmalidir. PowerShell'i 'Run as administrator' ile acip tekrar deneyin."
    exit 1
}

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host " AdaPanel Guvenlik Sertlestirme (harden-adapanel.ps1)" -ForegroundColor Cyan
Write-Host " Idempotent - tekrar calistirilabilir. Yonetici yetkisi dogrulandi." -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

# Veri kokunu sabitle (kurulumun standart konumu).
$root = "C:\ProgramData\AdaPanel"

# =======================================================================================
# K1 - C:\ProgramData\AdaPanel ACL KILIDI
# =======================================================================================
# Problem: Bu klasor, ProgramData'nin varsayilan ACL'sini miras alir ve BUILTIN\Users'a
# (= her IIS app pool kimligi, IUSR, FTP/sistem kullanicilari) Read verir. Bu da
# data\adapanel.db (admin parola ozetleri, 2FA secret'lari), certs\panel.password ve
# keys\key-*.xml (ASP.NET DataProtection anahtarlari — hatta yazilabilir) dosyalarini
# herhangi bir barindirilmis sitenin PHP koduna acar -> tam sunucu ele gecirme.
# Cozum (panel servisiyle AYNI .NET yontemi): yalniz HASSAS alt klasorleri (data/keys/certs/config/
# backups/mail-certs/updates) kilitle. icacls DEGIL .NET ACL kullanilir: izin "bu klasor + alt ogeler"e KESIN
# uygulanir (icacls (OI)(CI)F bazi Server'larda inherit-only uygulayip SYSTEM erisimini -> SQLite ve
# DataProtection'i kiriyordu). KOK (root) klasore DOKUNULMAZ.
# runtimes TOPLU olarak kapatilmaz (IIS icin RX kalmali) ama icindeki HASSAS yollar K2'de tek tek
# kapatilir -- asagiya bak.

Write-Host ""
Write-Host "[K1] C:\ProgramData\AdaPanel ACL kilidi..." -ForegroundColor Yellow

if (-not (Test-Path -LiteralPath $root)) {
    Write-Warning "[K1] '$root' bulunamadi. AdaPanel bu sunucuda standart konumda kurulu degil gibi gorunuyor; K1 atlandi."
}
else {
    # Panel servisinin (DataRootHardeningService) acilista uyguladiginin AYNISI — burada elle de uygulanir.
    #
    # 'updates' SONRADAN EKLENDI ve sebebi olculdu: 0.8.4 kurulu makinede
    # C:\ProgramData\AdaPanel\updates DACL'i (A;CIID;FA;;;WD) tasiyordu -- Everyone = FullControl,
    # mirasla gelmis, koruma bayragi yok. O klasorde SYSTEM olarak CALISTIRILAN iki dosya duruyor:
    # apply-update.ps1 (zamanlanmis gorev, -UserId SYSTEM -RunLevel Highest) ve AdaPanelSetup.exe.
    # Klasor uzerindeki FullControl, dosya izinlerinden bagimsiz olarak "sil + yerine koy" hakki
    # verir; paket tarafindaki SHA256/yayinci/depo denetimlerinin hicbiri betige bakmaz.
    # Bu listeler AYRISMAMALI: capraz kontrol DataRootHardeningTests icinde.
    $sensitive = @("data", "keys", "certs", "config", "backups", "mail-certs", "updates", "downloads")
    $inherit   = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
    $noProp    = [System.Security.AccessControl.PropagationFlags]::None
    $allow     = [System.Security.AccessControl.AccessControlType]::Allow
    $fullCtrl  = [System.Security.AccessControl.FileSystemRights]::FullControl
    $systemSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $adminSid  = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    $usersSid  = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
    $authSid   = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::AuthenticatedUserSid, $null)

    foreach ($sub in $sensitive) {
        $dir = Join-Path $root $sub
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        try {
            # K4 DUZELTMESI: DACL SIFIRDAN kurulur. Eskiden SetAccessRuleProtection($true, $true) ile
            # miras ACIK KOPYA olarak korunuyordu; kokte ne varsa (bu makinede OLCULDU:
            # Everyone = FullControl) hassas klasore KALICI explicit ACE olarak gomuluyor ve miras
            # kapali oldugu icin kok duzeltilse bile BIR DAHA DUSMUYORDU. Simdi listede olmayan
            # hicbir hesap hayatta kalmaz.
            $acl = New-Object System.Security.AccessControl.DirectorySecurity
            $acl.SetAccessRuleProtection($true, $false)
            # SYSTEM + Administrators: bu KLASOR + alt ogelere tam yetki (PropagationFlags.None = nesnenin
            # KENDISINE de uygulanir -> SYSTEM db/anahtar YAZABILIR; icacls inherit-only belirsizligi yok).
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($systemSid, $fullCtrl, $inherit, $noProp, $allow)))
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($adminSid,  $fullCtrl, $inherit, $noProp, $allow)))
            Set-Acl -LiteralPath $dir -AclObject $acl
            Write-Host "[K1]   Kilitlendi: $dir" -ForegroundColor Green
        }
        catch {
            Write-Warning "[K1]   '$dir' ACL ayarlanamadi: $($_.Exception.Message)"
        }
    }

    # AdaPanel servisi LocalSystem disinda OZEL bir hesapla calisiyorsa, o hesaba da hassas klasorlerde tam
    # yetki ver (yoksa panel kendi DB/anahtarlarina erisemez). LocalSystem (SYSTEM) zaten yetkilidir.
    $svc = Get-CimInstance Win32_Service -Filter "Name='AdaPanel' OR Name LIKE 'AdaPanel%'" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($svc -and -not ([string]::IsNullOrWhiteSpace($svc.StartName) -or $svc.StartName -ieq "LocalSystem" -or $svc.StartName -ieq ".\LocalSystem")) {
        Write-Host "[K1] Servis ozel hesapla calisiyor ('$($svc.StartName)'); hassas klasorlerde tam yetki veriliyor..."
        try {
            $svcSid = (New-Object System.Security.Principal.NTAccount($svc.StartName)).Translate([System.Security.Principal.SecurityIdentifier])
            foreach ($sub in $sensitive) {
                $dir = Join-Path $root $sub
                if (Test-Path -LiteralPath $dir) {
                    $acl = Get-Acl -LiteralPath $dir
                    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($svcSid, $fullCtrl, $inherit, $noProp, $allow)))
                    Set-Acl -LiteralPath $dir -AclObject $acl
                }
            }
        }
        catch {
            Write-Warning "[K1] Servis hesabi '$($svc.StartName)' icin izin verilemedi: $($_.Exception.Message). Hesap adini dogrulayin."
        }
    }

    Write-Host "[K1] Tamamlandi. (Panel servisi de acilista ayni sertlestirmeyi uygular.)" -ForegroundColor Green

    # ===================================================================================
    # K2-DATA - runtimes ICINDEKI HASSAS YOLLARI KIRACIYA KAPAT
    # ===================================================================================
    # Problem (OLCULDU): runtimes\mariadb\11\data\mysql\global_priv.frm YUKSELTILMEMIS bir
    # kullanici olarak ACILDI (2183 bayt). Ayni agacta butun musteri veritabani dosyalari var;
    # kiraci PHP'si veri dizinini kopyalayip tum veritabanlarini ve root parola ozetlerini alabilir.
    # bind-address=127.0.0.1 bu yolu HIC engellemez. Ayni sinifta runtimes\stalwart: posta
    # sunucusunun super-admin kimligi orada durur.
    #
    # "Bu yola kim erismek zorunda" sorusu koddan cevaplanmistir; HEPSINI kapatmak IIS'i KIRAR:
    #   php      -> IIS FastCGI php-cgi.exe'yi UYGULAMA HAVUZU KIMLIGIYLE calistirir  -> DOKUNMA
    #   nodejs   -> zamanlanmis gorevler SINIRLI YEREL KULLANICI olarak calisir       -> DOKUNMA
    #   python   -> ayni sekilde                                                      -> DOKUNMA
    #   nginx/redis/memcached/clamav/docker -> denetimde bulgu yok, korlemesine kapatma
    #   <motor>\<surum>\data -> yalniz motorun servis hesabi okur, kiraci TCP ile baglanir -> KAPAT
    #   stalwart, stalwart-cli, vandelay -> yalniz SYSTEM (gorev) ve panel kullanir        -> KAPAT
    #
    # Hassas veri kokunden FARKI: burada DACL sifirdan KURULMAZ, mevcut ACE'ler korunup yalniz
    # kiraci gruplari dusurulur. NEDEN: PostgreSQL/EDB kurulumu veri dizinine kendi servis hesabini
    # (genelde NETWORK SERVICE) explicit ACE ile yazar; sifirdan kurmak motoru baslatilamaz yapardi.

    Write-Host ""
    Write-Host "[K2-DATA] runtimes icindeki hassas yollar kiracıya kapatiliyor..." -ForegroundColor Yellow

    $runtimesRoot = Join-Path $root "runtimes"
    if (-not (Test-Path -LiteralPath $runtimesRoot)) {
        Write-Host "[K2-DATA] '$runtimesRoot' yok; atlandi."
    }
    else {
        $everyoneSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::WorldSid, $null)
        $guestsSid   = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinGuestsSid, $null)
        $iisSid      = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-568")  # IIS_IUSRS

        # Kapatilacak yollar (goreli). Veri dizinleri surumlu klasorun ALTINDA olabilir.
        $lockTargets = @()
        foreach ($panelOwned in "stalwart", "stalwart-cli", "vandelay") {
            $candidate = Join-Path $runtimesRoot $panelOwned
            if (Test-Path -LiteralPath $candidate) { $lockTargets += $candidate }
        }
        foreach ($engine in "mariadb", "mysql", "postgresql") {
            $engineRoot = Join-Path $runtimesRoot $engine
            if (-not (Test-Path -LiteralPath $engineRoot)) { continue }
            # Surumsuz: <engine>\data ; Surumlu: <engine>\<surum>\data
            $direct = Join-Path $engineRoot "data"
            if (Test-Path -LiteralPath $direct) { $lockTargets += $direct }
            foreach ($versionDir in Get-ChildItem -LiteralPath $engineRoot -Directory -ErrorAction SilentlyContinue) {
                $versioned = Join-Path $versionDir.FullName "data"
                if (Test-Path -LiteralPath $versioned) { $lockTargets += $versioned }
            }
        }

        if ($lockTargets.Count -eq 0) {
            Write-Host "[K2-DATA] Kapatilacak bir yol bulunamadi (veritabani/posta bileseni kurulu degil)."
        }

        foreach ($target in ($lockTargets | Sort-Object -Unique)) {
            try {
                $acl = Get-Acl -LiteralPath $target
                # Mirasi kapat ama mevcut ACE'leri KORU (motorun servis hesabi dusmesin).
                $acl.SetAccessRuleProtection($true, $true)
                $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($systemSid, $fullCtrl, $inherit, $noProp, $allow)))
                $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($adminSid,  $fullCtrl, $inherit, $noProp, $allow)))
                # Kiraciyi temsil eden her grubu DUSUR.
                [void]$acl.PurgeAccessRules($usersSid)
                [void]$acl.PurgeAccessRules($authSid)
                [void]$acl.PurgeAccessRules($everyoneSid)
                [void]$acl.PurgeAccessRules($guestsSid)
                [void]$acl.PurgeAccessRules($iisSid)
                Set-Acl -LiteralPath $target -AclObject $acl
                Write-Host "[K2-DATA]   Kilitlendi: $target" -ForegroundColor Green
            }
            catch {
                Write-Warning "[K2-DATA]   '$target' ACL ayarlanamadi: $($_.Exception.Message)"
            }
        }

        Write-Host "[K2-DATA] Tamamlandi. (Panel servisi de acilista ayni yollari kapatir.)" -ForegroundColor Green
    }

    # ===================================================================================
    # K1-TOOLS - tools ICINDEKI SYSTEM-only ARAC DIZINLERINI KIRACIYA KAPAT
    # ===================================================================================
    # Problem (OLCULDU): tools\win-acme DACL'i Everyone:(I)(CI)(F) tasiyordu. Klasor uzerindeki
    # FullControl, wacs.exe'nin kendi izni (Users:RX) ne olursa olsun "sil + yerine koy" ve DLL
    # dusurme hakki verir. wacs.exe panel (SYSTEM) tarafindan hem elle yenilemede hem gozetimsiz
    # otomasyonda calistirilir -> kiraci SYSTEM olur (updates klasorunun tipatip kardesi).
    #
    # tools TOPLUCA kapatilamaz: phpmyadmin/roundcube IIS ile KIRACI havuz kimligiyle sunulur,
    # composer/wp-cli kiracinin SINIRLI kimligiyle kosar. Bu yuzden yalniz SYSTEM-only alt dizinler
    # (win-acme) tek tek kapatilir. Liste DataRootHardeningService.LockedToolDirectories ile ayni.

    Write-Host ""
    Write-Host "[K1-TOOLS] tools icindeki SYSTEM-only arac dizinleri kiracıya kapatiliyor..." -ForegroundColor Yellow

    $toolsRoot = Join-Path $root "tools"
    if (-not (Test-Path -LiteralPath $toolsRoot)) {
        Write-Host "[K1-TOOLS] '$toolsRoot' yok; atlandi."
    }
    else {
        $everyoneToolsSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::WorldSid, $null)
        $guestsToolsSid   = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinGuestsSid, $null)
        $iisToolsSid      = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-568")  # IIS_IUSRS

        foreach ($toolName in "win-acme") {
            $target = Join-Path $toolsRoot $toolName
            if (-not (Test-Path -LiteralPath $target)) { continue }
            try {
                $acl = Get-Acl -LiteralPath $target
                # Mirasi kapat ama mevcut ACE'leri KORU (SYSTEM/servis dusmesin).
                $acl.SetAccessRuleProtection($true, $true)
                $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($systemSid, $fullCtrl, $inherit, $noProp, $allow)))
                $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($adminSid,  $fullCtrl, $inherit, $noProp, $allow)))
                [void]$acl.PurgeAccessRules($usersSid)
                [void]$acl.PurgeAccessRules($authSid)
                [void]$acl.PurgeAccessRules($everyoneToolsSid)
                [void]$acl.PurgeAccessRules($guestsToolsSid)
                [void]$acl.PurgeAccessRules($iisToolsSid)
                Set-Acl -LiteralPath $target -AclObject $acl
                Write-Host "[K1-TOOLS]   Kilitlendi: $target" -ForegroundColor Green
            }
            catch {
                Write-Warning "[K1-TOOLS]   '$target' ACL ayarlanamadi: $($_.Exception.Message)"
            }
        }

        Write-Host "[K1-TOOLS] Tamamlandi. (Panel servisi de acilista ayni yollari kapatir.)" -ForegroundColor Green
    }
}

# =======================================================================================
# K3 - PAYLASIMLI php.ini SERTLESTIRME
# =======================================================================================
# Problem: Paylasimli php.ini'de disable_functions BOS (exec/shell_exec/system acik) ve
# expose_php=On. Bos disable_functions, K1'deki dosya-okuma acigini tam RCE'ye ceviren
# seydir. Cozum: yalnizca OS komut calistirma fonksiyonlarini kapat + surumu gizle.
# max_execution_time'a DOKUNMUYORUZ (operator buyuk importlar icin bilerek ucsuz tutuyor).

Write-Host ""
Write-Host "[K3] Paylasimli PHP php.ini sertlestirme..." -ForegroundColor Yellow

# Anahtar-bazli (key-based) guncelleme yardimcisi:
#   - Aktif "name = ..." satiri varsa onu degistir.
#   - Yalnizca yorumlanmis ";name = ..." satiri varsa onu degistir + yorumdan cikar.
#   - Hicbiri yoksa dosyanin sonuna ekle.
# Dosyanin geri kalanini oldugu gibi korur. Bu sekilde idempotent ve tekrar-guvenlidir.
function Set-IniDirective {
    param(
        [Parameter(Mandatory = $true)][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $newLine = "$Name = $Value"
    # Anahtar adini regex icin kacir (PHP yonergelerinde ozel karakter olmaz ama garanti olsun).
    $escaped = [regex]::Escape($Name)
    # Aktif satir: satir basinda (bosluklar olabilir) name ardindan '='.
    $activePattern = '^\s*' + $escaped + '\s*='
    # Yorumlu satir: basinda ';' (bir veya birden fazla) olan ayni yonerge.
    $commentedPattern = '^\s*;+\s*' + $escaped + '\s*='

    $result = New-Object System.Collections.Generic.List[string]
    $replaced = $false

    foreach ($line in $Lines) {
        if (-not $replaced -and $line -match $activePattern) {
            # Aktif yonergeyi yeni degerle degistir.
            $result.Add($newLine)
            $replaced = $true
        }
        elseif (-not $replaced -and $line -match $commentedPattern) {
            # Yorumlu yonergeyi acip yeni degerle degistir.
            $result.Add($newLine)
            $replaced = $true
        }
        else {
            $result.Add($line)
        }
    }

    if (-not $replaced) {
        # Yonerge dosyada hic yoksa sona ekle.
        $result.Add($newLine)
    }

    return , $result.ToArray()
}

# Sertlestirilecek yonergeler.
# disable_functions: SADECE OS komut calistirma fonksiyonlari. curl_exec, parse_ini_file,
# fopen vb. KASITLI OLARAK dahil EDILMEDI — normal uygulamalari bozarlardi.
$phpDirectives = [ordered]@{
    "disable_functions" = "exec,passthru,shell_exec,system,proc_open,popen,pcntl_exec"
    "expose_php"        = "Off"
}

# 1) php.ini dosyalarini bul: once standart runtime konumu, yoksa best-effort Program Files.
$phpIniFiles = @()
$phpRuntimeRoot = Join-Path $root "runtimes\php"
if (Test-Path -LiteralPath $phpRuntimeRoot) {
    $phpIniFiles = Get-ChildItem -LiteralPath $phpRuntimeRoot -Recurse -Filter "php.ini" -ErrorAction SilentlyContinue
}

if (-not $phpIniFiles -or $phpIniFiles.Count -eq 0) {
    # Yedek (best-effort): Program Files altindaki PHP kurulumlari.
    Write-Warning "[K3] '$phpRuntimeRoot' altinda php.ini bulunamadi; Program Files altinda araniyor (best-effort)..."
    foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($pf)) { continue }
        $pfPhp = Join-Path $pf "PHP"
        if (Test-Path -LiteralPath $pfPhp) {
            $phpIniFiles += Get-ChildItem -LiteralPath $pfPhp -Recurse -Filter "php.ini" -ErrorAction SilentlyContinue
        }
    }
}

if (-not $phpIniFiles -or $phpIniFiles.Count -eq 0) {
    Write-Warning "[K3] Hic php.ini bulunamadi. PHP runtime'i baska bir konumdaysa o php.ini'leri elle sertlestirin."
}
else {
    # Ayni dosyayi iki kez islemeyelim (yedek arama cakisirsa diye benzersizlestir).
    $phpIniFiles = $phpIniFiles | Sort-Object FullName -Unique
    Write-Host "[K3] $($phpIniFiles.Count) adet php.ini bulundu. Guncelleniyor..."

    foreach ($ini in $phpIniFiles) {
        try {
            # 2) Dosyayi oku, anahtar-bazli guncelle, geri yaz (UTF8).
            $lines = Get-Content -LiteralPath $ini.FullName -ErrorAction Stop
            foreach ($name in $phpDirectives.Keys) {
                $lines = Set-IniDirective -Lines $lines -Name $name -Value $phpDirectives[$name]
            }
            Set-Content -LiteralPath $ini.FullName -Value $lines -Encoding UTF8 -ErrorAction Stop
            Write-Host "[K3]   Sertlestirildi: $($ini.FullName)" -ForegroundColor Green
        }
        catch {
            Write-Warning "[K3]   '$($ini.FullName)' guncellenemedi: $($_.Exception.Message)"
        }
    }

    # 3) Degisikliklerin hemen gecerli olmasi icin FastCGI'yi (php-cgi.exe) geri dondur.
    #    Calisan surec yoksa taskkill hata verir; yutuyoruz (idempotent).
    Write-Host "[K3] FastCGI yenileniyor (php-cgi.exe sonlandiriliyor; IIS otomatik yeniden baslatir)..."
    try {
        & taskkill /f /im php-cgi.exe 2>&1 | Out-Null
        Write-Host "[K3] php-cgi.exe surecleri sonlandirildi (veya zaten calismiyordu)."
    }
    catch {
        Write-Host "[K3] php-cgi.exe calismiyordu; geri dondurme adimi atlandi."
    }
    Write-Host "[K3] Tamamlandi." -ForegroundColor Green
}

# =======================================================================================
# B2 - YINELENEN (DUPLICATE) GUVENLIK DUVARI KURALLARINI TEMIZLE
# =======================================================================================
# Problem: Site/webdb guvenlik duvari kurallari, eskileri silinmeden tekrar ekleniyor;
# bu da ayni gelen (inbound) kuraldan ~9 adet birakir (orn. phpMyAdmin TCP 8896).
# Cozum: "AdaPanel" ile baslayan gelen kurallari DisplayName'e gore grupla; her grupta
# BIR tane birak, fazlalari kaldir. DisplayName'ler cakistigi icin silme islemi kuralin
# benzersiz Name (InstanceID) alaniyla yapilir.

Write-Host ""
Write-Host "[B2] Yinelenen AdaPanel guvenlik duvari kurallari temizleniyor..." -ForegroundColor Yellow

try {
    $adaRules = Get-NetFirewallRule -Direction Inbound -ErrorAction Stop |
        Where-Object { $_.DisplayName -like 'AdaPanel*' }

    if (-not $adaRules -or @($adaRules).Count -eq 0) {
        Write-Host "[B2] 'AdaPanel*' ile eslesen gelen kural bulunamadi; yapilacak bir sey yok."
    }
    else {
        $groups = $adaRules | Group-Object -Property DisplayName
        $totalRemoved = 0

        foreach ($group in $groups) {
            if ($group.Count -le 1) {
                # Tek kural -> duplicate yok, dokunma.
                continue
            }

            # Bir tanesini koru, gerisini kaldir. Hangisi kaldigi onemli degil (ayni kural).
            $toRemove = $group.Group | Select-Object -Skip 1
            $removedHere = 0

            foreach ($rule in $toRemove) {
                try {
                    # Name = benzersiz InstanceID; DisplayName ile silmek hepsini silerdi.
                    Remove-NetFirewallRule -Name $rule.Name -ErrorAction Stop
                    $removedHere++
                }
                catch {
                    Write-Warning "[B2]   '$($group.Name)' icin kural ($($rule.Name)) kaldirilamadi: $($_.Exception.Message)"
                }
            }

            if ($removedHere -gt 0) {
                Write-Host "[B2]   '$($group.Name)': $removedHere yinelenen kural kaldirildi (1 tane korundu)." -ForegroundColor Green
                $totalRemoved += $removedHere
            }
        }

        if ($totalRemoved -eq 0) {
            Write-Host "[B2] Yinelenen kural bulunmadi; tum AdaPanel kurallari zaten benzersiz."
        }
        else {
            Write-Host "[B2] Toplam $totalRemoved yinelenen kural kaldirildi." -ForegroundColor Green
        }
    }
    Write-Host "[B2] Tamamlandi." -ForegroundColor Green
}
catch {
    Write-Warning "[B2] Guvenlik duvari kurallari islenirken hata: $($_.Exception.Message)"
}

# =======================================================================================
# B6 - WEB (80/443) VE DNS (53) ICIN ACIK GUVENLIK DUVARI KURALLARI
# =======================================================================================
# Problem: Barindirilan sitelerin 80/443 ve (Windows DNS rolu kuruluysa) 53 portlari icin
# acik kural yok; IIS'in varsayilan kurali genelde 443'u (HTTPS) ACMAZ -> sitelere HTTPS
# ile erisilemez, Let's Encrypt HTTP-01 dogrulamasi 80'de takilir. Cozum: idempotent
# (once-sil-sonra-ekle) gelen Allow kurallari. Installer'daki configure-firewall.ps1 ile ayni.

Write-Host ""
Write-Host "[B6] Web (80/443) ve DNS (53) guvenlik duvari kurallari aciliyor..." -ForegroundColor Yellow

foreach ($webPort in 80, 443) {
    $webRule = "AdaPanel Web $webPort"
    try {
        Get-NetFirewallRule -DisplayName $webRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
        New-NetFirewallRule -DisplayName $webRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $webPort -Profile Any -ErrorAction Stop | Out-Null
        Write-Host "[B6]   '$webRule' olusturuldu." -ForegroundColor Green
    }
    catch {
        Write-Warning "[B6]   '$webRule' olusturulamadi: $($_.Exception.Message)"
    }
}

# Y10: 53 yalnizca DNS rolu GERCEKTEN kuruluysa acilir (dinleyicisi olmayan port acmayiz) ve rol
# varsa OZYINELEME KAPATILIR. Windows varsayilani ozyineleme ACIK oldugu icin, 53'u disariya acip
# ozyinelemeyi birakmak sunucuyu ilk gunden DNS amplifikasyon reflektoru yapar.
$dnsInstalled = $false
try { $dnsInstalled = $null -ne (Get-Service -Name "DNS" -ErrorAction Stop) } catch { $dnsInstalled = $false }

if (-not $dnsInstalled) {
    Write-Host "[B6]   Windows DNS Server rolu kurulu degil; 53 ACILMADI (varsa eski kural kaldiriliyor)." -ForegroundColor Yellow
    foreach ($dnsProto in 'TCP', 'UDP') {
        Get-NetFirewallRule -DisplayName "AdaPanel DNS 53 $dnsProto" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
    }
}
else {
    if (Get-Command Set-DnsServerRecursion -ErrorAction SilentlyContinue) {
        try {
            Set-DnsServerRecursion -Enable $false -ErrorAction Stop
            Write-Host "[B6]   DNS ozyinelemesi KAPATILDI (yalniz otoriter yanit; acik cozucu degil)." -ForegroundColor Green
        }
        catch {
            Write-Warning "[B6]   DNS ozyinelemesi kapatilamadi: $($_.Exception.Message). Elle: Set-DnsServerRecursion -Enable `$false"
        }
    }
    else {
        Write-Warning "[B6]   Set-DnsServerRecursion yok; ozyineleme KAPATILAMADI. 53 aciliyor ama sunucu acik cozucu olabilir."
    }

    foreach ($dnsProto in 'TCP', 'UDP') {
        $dnsRule = "AdaPanel DNS 53 $dnsProto"
        try {
            Get-NetFirewallRule -DisplayName $dnsRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
            New-NetFirewallRule -DisplayName $dnsRule -Direction Inbound -Action Allow -Protocol $dnsProto -LocalPort 53 -Profile Any -ErrorAction Stop | Out-Null
            Write-Host "[B6]   '$dnsRule' olusturuldu." -ForegroundColor Green
        }
        catch {
            Write-Warning "[B6]   '$dnsRule' olusturulamadi: $($_.Exception.Message)"
        }
    }
}
Write-Host "[B6] Tamamlandi." -ForegroundColor Green

# =======================================================================================
# B7 - phpMyAdmin/Adminer PUBLIC PORTLARINI KAPAT (8896/8897)
# =======================================================================================
# Problem (codex bulgusu): phpMyAdmin 8896 disariya ACIK + /composer.json, /vendor/composer/installed.json
# sizinti. Plesk'te bu araclar panel auth ARKASINDADIR. AdaPanel artik araci YALNIZ loopback'te (127.0.0.1)
# dinletir ve panelin kimlik-dogrulamali HTTPS reverse-proxy'si uzerinden sunar. Eski kurulumda acilmis
# public guvenlik duvari kurallarini (varsa) kaldiriyoruz; araclar panel uzerinden calismaya devam eder.

Write-Host ""
Write-Host "[B7] phpMyAdmin/Adminer public guvenlik duvari kurallari kaldiriliyor (8896/8897)..." -ForegroundColor Yellow
try {
    $webDbRules = Get-NetFirewallRule -Direction Inbound -ErrorAction Stop |
        Where-Object { $_.DisplayName -like 'AdaPanel WebDb*' }

    if (-not $webDbRules -or @($webDbRules).Count -eq 0) {
        Write-Host "[B7] Acik WebDb kurali yok; phpMyAdmin/Adminer zaten public degil."
    }
    else {
        foreach ($rule in $webDbRules) {
            try {
                Remove-NetFirewallRule -Name $rule.Name -ErrorAction Stop
                Write-Host "[B7]   Kaldirildi: '$($rule.DisplayName)' ($($rule.Name))." -ForegroundColor Green
            }
            catch {
                Write-Warning "[B7]   '$($rule.DisplayName)' kaldirilamadi: $($_.Exception.Message)"
            }
        }
        Write-Host "[B7] phpMyAdmin/Adminer artik yalniz panel uzerinden erisilebilir." -ForegroundColor Green
    }
    Write-Host "[B7] NOT: Loopback binding'in tam uygulanmasi icin panelden araci bir kez YENIDEN DAGIT." -ForegroundColor Yellow
}
catch {
    Write-Warning "[B7] WebDb guvenlik duvari kurallari islenirken hata: $($_.Exception.Message)"
}
Write-Host "[B7] Tamamlandi." -ForegroundColor Green

# =======================================================================================
# B8 - YONETIM/PAYLASIM PORTLARINI ENGELLE (opt-in: -HardenManagementPorts)
# =======================================================================================
# Problem (codex bulgusu): 445(SMB)/135(RPC)/137-139(NetBIOS)/5985-5986(WinRM) disariya acik. Bir hosting
# sunucusu disariya web(80/443)+mail+dns sunar; bu yonetim/dosya-paylasim portlari INTERNETE ACIK OLMAMALI
# (NTLM relay, kimlik dogrulama saldirilari, uzaktan yonetim yuzeyi). Bu portlar genelde Windows'un KENDI
# varsayilan kurallariyla aciktir; AdaPanel acmaz. Opt-in: bilerek -HardenManagementPorts verilirse Block
# kurallari ekleriz (Block, Allow'dan onceliklidir). Loopback (yerel) trafigi etkilenmez.

if ($HardenManagementPorts) {
    Write-Host ""
    Write-Host "[B8] Yonetim/paylasim portlari engelleniyor (SMB/RPC/NetBIOS/WinRM)..." -ForegroundColor Yellow
    $blockSpecs = @(
        @{ Name = "AdaPanel Block SMB 445";     Proto = "TCP"; Ports = @("445") },
        @{ Name = "AdaPanel Block RPC 135";     Proto = "TCP"; Ports = @("135") },
        @{ Name = "AdaPanel Block NetBIOS TCP"; Proto = "TCP"; Ports = @("137-139") },
        @{ Name = "AdaPanel Block NetBIOS UDP"; Proto = "UDP"; Ports = @("137-138") },
        @{ Name = "AdaPanel Block WinRM";       Proto = "TCP"; Ports = @("5985", "5986") }
    )
    foreach ($spec in $blockSpecs) {
        try {
            Get-NetFirewallRule -DisplayName $spec.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
            New-NetFirewallRule -DisplayName $spec.Name -Direction Inbound -Action Block -Protocol $spec.Proto -LocalPort $spec.Ports -Profile Any -ErrorAction Stop | Out-Null
            Write-Host "[B8]   Engellendi: $($spec.Name) ($($spec.Proto) $($spec.Ports -join ','))." -ForegroundColor Green
        }
        catch {
            Write-Warning "[B8]   '$($spec.Name)' eklenemedi: $($_.Exception.Message)"
        }
    }
    Write-Host "[B8] Tamamlandi." -ForegroundColor Green
}
else {
    Write-Host ""
    Write-Host "[B8] ATLANDI (varsayilan). SMB(445)/RPC(135)/NetBIOS/WinRM(5985) disariya ACIK olabilir." -ForegroundColor Yellow
    Write-Host "[B8] Internete kapatmak icin: .\harden-adapanel.ps1 -HardenManagementPorts" -ForegroundColor Yellow
    Write-Host "[B8] (WinRM'i uzaktan yonetim icin kullaniyorsan bu anahtari VERME.)" -ForegroundColor Yellow
}

# =======================================================================================
# RDP (3389) — SADECE belirtilen kaynak IP'ye kisitla (opt-in: -RdpAllowFrom)
# =======================================================================================
# RDP'yi blanket (tumden) KAPATMIYORUZ — kendini kilitleyebilirsin. -RdpAllowFrom verirsen yerlesik RDP
# kurallarinin RemoteAddress'ini o IP(ler)e cekeriz (digerleri dusurulur). Plesk de yonetim erisimini IP
# allowlist ile kisitlar. -RdpAllowFrom verilmezse RDP'ye DOKUNMAYIZ (sadece uyari).

Write-Host ""
if (-not [string]::IsNullOrWhiteSpace($RdpAllowFrom)) {
    $rdpSources = $RdpAllowFrom -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    Write-Host "[RDP] RDP (3389) erisimi yalniz su kaynaklara kisitlaniyor: $($rdpSources -join ', ')" -ForegroundColor Yellow
    try {
        $rdpRules = Get-NetFirewallRule -Direction Inbound -ErrorAction Stop |
            Where-Object { ($_.DisplayGroup -like '*Remote Desktop*' -or $_.DisplayName -like '*Remote Desktop*') -and $_.Enabled -eq 'True' }

        if (-not $rdpRules -or @($rdpRules).Count -eq 0) {
            # Yerlesik kural yoksa allowlist'li kendi Allow kuralimizi ekle (3389 yalniz bu kaynaklara).
            Get-NetFirewallRule -DisplayName "AdaPanel RDP Allowlist" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
            New-NetFirewallRule -DisplayName "AdaPanel RDP Allowlist" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 3389 -RemoteAddress $rdpSources -Profile Any -ErrorAction Stop | Out-Null
            Write-Host "[RDP]   'AdaPanel RDP Allowlist' eklendi (3389, kaynak: $($rdpSources -join ', '))." -ForegroundColor Green
        }
        else {
            foreach ($rule in $rdpRules) {
                try {
                    $rule | Set-NetFirewallRule -RemoteAddress $rdpSources -ErrorAction Stop
                    Write-Host "[RDP]   Kisitlandi: '$($rule.DisplayName)'." -ForegroundColor Green
                }
                catch {
                    Write-Warning "[RDP]   '$($rule.DisplayName)' kisitlanamadi: $($_.Exception.Message)"
                }
            }
        }
        Write-Host "[RDP] UYARI: Kaynak IP'n degisirse RDP'den KILITLENEBILIRSIN. Saglayicinin konsol/VNC erisimini hazir tut." -ForegroundColor Red
    }
    catch {
        Write-Warning "[RDP] RDP kurallari kisitlanirken hata: $($_.Exception.Message)"
    }
}
else {
    Write-Host "[RDP] ATLANDI (varsayilan). RDP (3389) tum kaynaklara ACIK olabilir." -ForegroundColor Yellow
    Write-Host "[RDP] Kisitlamak icin: .\harden-adapanel.ps1 -RdpAllowFrom '<senin-sabit-IP>'" -ForegroundColor Yellow
    Write-Host "[RDP] (Blanket kapatmiyoruz; kendini kilitlememen icin bilerek opt-in.)" -ForegroundColor Yellow
}

# =======================================================================================
# Bitti.
# =======================================================================================
Write-Host ""
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host " Sertlestirme tamamlandi: K1 (ACL), K3 (php.ini), B2+B6+B7 (firewall)." -ForegroundColor Cyan
$mgmtState = if ($HardenManagementPorts) { "uygulandi" } else { "ATLANDI (-HardenManagementPorts ile ac)" }
$rdpState  = if (-not [string]::IsNullOrWhiteSpace($RdpAllowFrom)) { "kisitlandi ($RdpAllowFrom)" } else { "ATLANDI (-RdpAllowFrom ile kisitla)" }
Write-Host " B8 (SMB/RPC/WinRM): $mgmtState" -ForegroundColor Cyan
Write-Host " RDP allowlist: $rdpState" -ForegroundColor Cyan
Write-Host " Script idempotenttir; gerekirse tekrar calistirabilirsiniz." -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

# =======================================================================================
# K2 - NEDEN BURADA YOK (MariaDB root parolasi)
# =======================================================================================
# Bu script BILEREK MariaDB root parolasini AYARLAMAZ/DEGISTIRMEZ.
#
# Sebep: Canli bir sistemde root parolasi belirlemek/degistirmek, panelin DB erisimini
# ANINDA kilitler — CUNKU AdaPanel kendi sakladigi DB kimlik bilgisiyle (config'teki
# kullanici/parola) baglanir. Parolayi burada degistirirsek, panelin sakladigi kimlik
# bilgisi AYNI ANDA guncellenmedigi surece panel veritabanina baglanamaz hale gelir
# (siteler, e-posta, webdb araclari vb. coker).
#
# Dogru yontem: K2, panelin/installer'in akisi uzerinden yapilmalidir. Orada hem MariaDB
# parolasi ayarlanir HEM DE panelin sakladigi kimlik bilgisi tek bir atomik adimda
# guncellenir; boylece baglanti kopmaz. Bu yuzden K2 kasitli olarak bu scripte DAHIL
# EDILMEMISTIR.
