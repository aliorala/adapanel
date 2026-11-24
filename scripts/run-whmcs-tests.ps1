# WHMCS/PHP FATURALAMA MODULUNUN TESTLERI -- tek giris noktasi.
#
# NEDEN VAR: `integrations/whmcs` altindaki 1430 satir PHP musteriye gidiyor ama uzun sure
# CALISTIRILMADI; `php` bu makinede PATH'te yok, dolayisiyla testleri "elle su komutu yaz" diye
# birakmak, pratikte hic kosmamalari demekti. Depoda bunun bedeli olculu: 0.7.0-0.7.2'de
# `.htaccess` IIS modulu YESIL testlerle yayinlandi ama pakete hic girmemisti -- yesil test,
# TESLIM EDILMIS anlamina gelmez; teslim/kosum yolunun kendisi de olculmelidir. Bu betik o yol.
#
# NE YAPAR:
#   1. PHP'yi BULUR (panelin barindirma icin kurdugu surum; components.json'daki php-8.3 girdisi).
#   2. `php -l` ile modulun ve testlerin sozdizimini olcer.
#   3. Davranis testlerini kosar.
#
# PHP YOKSA SESSIZCE YESIL GECMEZ: hata koduyla ve nerede aranmasi gerektigini yazarak durur.
# "Test kosmadi" ile "test gecti" ayni sey degildir; farki gizleyen bir betik, olculmemis kodu
# olculmus gibi gosterir.
#
# KULLANIM:
#   powershell -ExecutionPolicy Bypass -File scripts\run-whmcs-tests.ps1
#   powershell ... -File scripts\run-whmcs-tests.ps1 -PhpPath "C:\php\php.exe"

[CmdletBinding()]
param(
    # Kendi PHP'nizi verin; bos birakilirsa asagidaki sirayla aranir.
    [string] $PhpPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Find-AdaPhp {
    <#
        PHP'yi ARAR, VARSAYMAZ. Sira bilincli: once panelin KENDI kurdugu surum, cunku modulun
        birlikte calisacagi PHP odur; sonra PATH; sonra yaygin elle kurulum yerleri.
    #>
    param([string] $Istenen)

    if ($Istenen -ne '') {
        # Elle verilen yol yanlissa ARAMAYA DEVAM EDILMEZ: sessizce baska bir PHP'ye dusmek,
        # "verdigim surumle test ettim" sanan kisiye yanlis bilgi vermek olurdu.
        if (-not (Test-Path -LiteralPath $Istenen)) {
            Write-Host "WHMCS modul testleri KOSULAMADI: verilen PHP bulunamadi." -ForegroundColor Red
            Write-Host "  -PhpPath $Istenen"
            Write-Host 'BU BIR BASARI DEGILDIR: modul olculmedi.' -ForegroundColor Red
            exit 2
        }
        return (Resolve-Path -LiteralPath $Istenen).Path
    }

    $adaylar = @()

    # Panelin barindirma icin kurdugu PHP (components.json -> php-8.3 -> installDir).
    $phpKok = Join-Path $env:ProgramData 'AdaPanel\runtimes\php'
    if (Test-Path -LiteralPath $phpKok) {
        # En yeni surum once: klasor adlari 8.3, 8.4 ... seklinde.
        $adaylar += Get-ChildItem -LiteralPath $phpKok -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'php.exe' }
    }

    $yolda = Get-Command php -ErrorAction SilentlyContinue
    if ($null -ne $yolda) { $adaylar += $yolda.Source }

    $adaylar += @('C:\php\php.exe', 'C:\tools\php\php.exe')

    foreach ($aday in $adaylar) {
        if ($aday -and (Test-Path -LiteralPath $aday)) { return $aday }
    }

    return ''
}

$php = Find-AdaPhp -Istenen $PhpPath

if ($php -eq '') {
    Write-Host 'WHMCS modul testleri KOSULAMADI: PHP bulunamadi.' -ForegroundColor Red
    Write-Host ''
    Write-Host 'Arananlar:'
    Write-Host "  - $(Join-Path $env:ProgramData 'AdaPanel\runtimes\php\<surum>\php.exe')  (panelin kurdugu PHP)"
    Write-Host '  - PATH uzerindeki php'
    Write-Host '  - C:\php\php.exe , C:\tools\php\php.exe'
    Write-Host ''
    Write-Host 'Cozum: paneli PHP bileseniyle kurun (Bilesenler -> PHP 8.3), ya da -PhpPath verin.'
    Write-Host 'BU BIR BASARI DEGILDIR: modul olculmedi.' -ForegroundColor Red
    exit 2
}

Write-Host "PHP: $php"
$surum = & $php -r 'echo PHP_VERSION;'
Write-Host "Surum: $surum"

# VIRUS KONTROLU. Bu makinede PE dosyalarina bulasan bir infector cikmisti ve testleri kosan sey
# bir PE. Beklenen imza yoksa DURULUR: bulasik bir yorumlayicinin "hepsi gecti" demesi, testin
# kendisinden daha kotu bir yalan olurdu.
$imza = (Get-Item -LiteralPath $php).VersionInfo.CompanyName
if ($imza -ne 'The PHP Group') {
    Write-Host "DUR: php.exe CompanyName alani beklenmedik: '$imza' (beklenen: 'The PHP Group')." -ForegroundColor Red
    Write-Host 'Bu makinede daha once aktif bir PE infector goruldu. Dosyayi dogrulamadan kosmayin.'
    exit 3
}

$testDizini = Join-Path $repoRoot 'integrations\whmcs'
$hata = 0

Write-Host ''
Write-Host '--- php -l (sozdizimi) ---'
Get-ChildItem -LiteralPath $testDizini -Recurse -Filter '*.php' | ForEach-Object {
    $goreli = $_.FullName.Substring($repoRoot.Length + 1)
    $cikti = & $php -l $_.FullName
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  HATA : $goreli" -ForegroundColor Red
        Write-Host "         $cikti"
        $script:hata++
    } else {
        Write-Host "  temiz: $goreli"
    }
}

Write-Host ''
Write-Host '--- davranis testleri ---'

# `-d disable_functions=` SART. Panelin php.ini'si barindirma sertlestirmesi olarak `proc_open`i
# KAPATIR (dogru bir karar, degistirilmemeli); oysa sozlesme testi sahte paneli ayri bir surec
# olarak baslatmak zorunda. Kisitlama YALNIZ test kosumunda, YALNIZ komut satirinda gevsetilir --
# diskteki php.ini'ye dokunulmaz, yani barindirilan sitelerin ayari degismez.
$testler = @(
    'integrations\whmcs\tests\leftover-note.test.php',
    'integrations\whmcs\tests\module-contract.test.php'
)

# A standalone test PHP may have no php.ini. Enable its bundled cURL only for
# these test processes; never change the hosting runtime's persistent settings.
$testPhpArgs = @('-d', 'disable_functions=')
$curlLoaded = & $php -r 'echo extension_loaded("curl") ? "yes" : "no";'
if ($curlLoaded -ne 'yes') {
    $extensionDir = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $php).Path) 'ext'
    if (-not (Test-Path -LiteralPath (Join-Path $extensionDir 'php_curl.dll'))) {
        throw 'WHMCS tests require the PHP cURL extension; no bundled php_curl.dll was found.'
    }
    $testPhpArgs += @('-d', "extension_dir=$extensionDir", '-d', 'extension=curl')
}

foreach ($test in $testler) {
    Write-Host ''
    Write-Host "== $test"
    & $php @testPhpArgs (Join-Path $repoRoot $test)
    if ($LASTEXITCODE -ne 0) {
        $hata++
    }
}

Write-Host ''
if ($hata -gt 0) {
    Write-Host "SONUC: $hata adim BASARISIZ" -ForegroundColor Red
    exit 1
}

Write-Host 'SONUC: WHMCS modulu temiz' -ForegroundColor Green
exit 0
