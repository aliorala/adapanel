<#
.SYNOPSIS
    AdaPanel canli (production) Windows VPS guvenlik sertlestirme scripti.

.DESCRIPTION
    Bu script, kurulumu YENIDEN YAPMADAN canli bir AdaPanel sunucusundaki uc kritik
    guvenlik acigini kapatir. Sunucu yoneticisi tarafindan ELLE ve Administrator olarak
    calistirilir. Tum islemler IDEMPOTENT'tir: tekrar tekrar calistirmak guvenlidir.

    Uygulanan duzeltmeler:
      K1 - C:\ProgramData\AdaPanel ACL'lerini kilitle (hassas dosyalari BUILTIN\Users'tan koru)
      K3 - Paylasimli PHP php.ini sertlestir (OS komut fonksiyonlarini kapat + surum gizle)
      B2 - Yinelenen (duplicate) Windows Guvenlik Duvari kurallarini temizle

    NOT: MariaDB root parolasi BILEREK DEGISTIRILMEZ (sondaki "K2 - NEDEN BURADA YOK"
    bolumune bakin).

.NOTES
    Hedef: CANLI VPS. Bu makinede DEGIL, sunucuda calistirilmak uzere yazilmistir.
#>

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
# Cozum: Mirasi kir, sadece SYSTEM + Administrators full kalsin; panelin kendi servis
# hesabini ve calismasi gereken runtime ikililerini ise tekrar ac.

Write-Host ""
Write-Host "[K1] C:\ProgramData\AdaPanel ACL kilidi..." -ForegroundColor Yellow

if (-not (Test-Path -LiteralPath $root)) {
    Write-Warning "[K1] '$root' bulunamadi. AdaPanel bu sunucuda standart konumda kurulu degil gibi gorunuyor; K1 atlandi."
}
else {
    # 1) Mirasi kir ve SADECE SYSTEM + Administrators full control olacak sekilde sifirla.
    #    /inheritance:r  -> miras alinan izinleri kaldirir (acik izinlere cevirir)
    #    /grant:r        -> mevcut acik izinleri bu hesaplar icin SIFIRLAYIP yeniden verir
    #    (OI)(CI)F       -> Object+Container Inherit, Full control (alt klasor/dosyalara da)
    #    /T              -> mevcut tum alt ogelere uygula
    #    /C              -> hatalarda durmadan devam et
    Write-Host "[K1] Miras kiriliyor; SYSTEM + Administrators tam yetki olarak sifirlaniyor..."
    & icacls "$root" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" /T /C
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "[K1] icacls sifirlama bazi ogelerde uyari/hata dondurdu (exit $LASTEXITCODE). Cikti yukarida; genelde acik dosyalardan kaynaklanir."
    }

    # 2) KRITIK — panelin kendi servisini kilitleme. AdaPanel servisi LocalSystem disinda
    #    ozel bir hesapla (orn. bir gMSA / yerel hesap) calisiyorsa, mirasi kirdiktan sonra
    #    erisimini kaybeder. Servis hesabini tespit edip gerekirse tam yetki veriyoruz.
    #    Servis "AdaPanel" veya "AdaPanelApi" olabilir; ikisini de yakalariz.
    $svc = Get-CimInstance Win32_Service -Filter "Name='AdaPanel' OR Name LIKE 'AdaPanel%'" -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($svc) {
        $startName = $svc.StartName
        Write-Host "[K1] AdaPanel servisi bulundu: '$($svc.Name)' (LogOn: '$startName')."

        # LocalSystem zaten SYSTEM olarak tam yetkili; ekstra grant gerekmez.
        $isLocalSystem = [string]::IsNullOrWhiteSpace($startName) -or
                         $startName -ieq "LocalSystem" -or
                         $startName -ieq ".\LocalSystem"

        if ($isLocalSystem) {
            Write-Host "[K1] Servis LocalSystem/SYSTEM olarak calisiyor; SYSTEM tam yetkili oldugundan ek izin gerekmiyor."
        }
        else {
            Write-Host "[K1] Servis ozel bir hesapla calisiyor ('$startName'); kilitlenmemesi icin tam yetki veriliyor..."
            & icacls "$root" /grant:r "$($startName):(OI)(CI)F" /T /C
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "[K1] Servis hesabi '$startName' icin izin verme uyari/hata dondurdu (exit $LASTEXITCODE). Hesap adini ('DOMAIN\kullanici' veya '.\kullanici' formatinda olmali) dogrulayin."
            }
        }
    }
    else {
        Write-Warning "[K1] AdaPanel servisi bulunamadi (Win32_Service Name='AdaPanel'/'AdaPanel%'). Servis baska bir adla calisiyorsa, o hesabin '$root' uzerinde erisimi olup olmadigini elle kontrol edin."
    }

    # 3) Sadece hosted sitelerin CALISMASI icin gereken runtime'lara (php, node) Read+Execute
    #    geri ver. MariaDB/Redis/Stalwart AG uzerinden (localhost:3306/6379/...) erisilir;
    #    dosyalarinin (DB verisi/config/parola) tenant'larca OKUNMASINA GEREK YOK ve gizli
    #    kalmali — bu yuzden RX'i TUM runtimes'a degil, yalniz php + node'a veriyoruz.
    #    data/keys/certs/config/backups/mail-certs'e de DOKUNMUYORUZ (kilitli ebeveyni miras alir).
    $runtimes = Join-Path $root "runtimes"
    $reopened = @()
    foreach ($sub in @("php", "node")) {
        $dir = Join-Path $runtimes $sub
        if (Test-Path -LiteralPath $dir) {
            Write-Host "[K1] '$dir' uzerinde IIS_IUSRS + Users icin Read+Execute (RX) geri veriliyor..."
            & icacls "$dir" /grant:r "BUILTIN\IIS_IUSRS:(OI)(CI)RX" "BUILTIN\Users:(OI)(CI)RX" /T /C
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "[K1] '$dir' RX izni uyari/hata dondurdu (exit $LASTEXITCODE)."
            }
            $reopened += $dir
        }
    }
    if ($reopened.Count -eq 0) {
        Write-Warning "[K1] runtimes\php / runtimes\node bulunamadi; RX yeniden acma atlandi (PHP/Node baska konumdaysa elle ac)."
    }

    # 4) Yonetici dogrulamasi icin son ACL durumlarini bas.
    Write-Host ""
    Write-Host "[K1] Son durum -> icacls `"$root`":" -ForegroundColor Green
    & icacls "$root"
    foreach ($dir in $reopened) {
        Write-Host ""
        Write-Host "[K1] Son durum -> icacls `"$dir`":" -ForegroundColor Green
        & icacls "$dir"
    }
    Write-Host "[K1] Tamamlandi." -ForegroundColor Green
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
Write-Host "[B6] Tamamlandi." -ForegroundColor Green

# =======================================================================================
# Bitti.
# =======================================================================================
Write-Host ""
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host " Sertlestirme tamamlandi: K1 (ACL), K3 (php.ini), B2+B6 (firewall)." -ForegroundColor Cyan
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
