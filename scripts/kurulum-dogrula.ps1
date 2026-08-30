# KURULUM SONRASI DOGRULAMA -- "paket kuruldu" ile "duzeltme uygulandi" ayni sey degil.
#
# NEDEN VAR:
# Bu depoda ayni ders uc kez odendi. En pahalisi e-posta sunucusuydu: kurulum "basarili" diyordu,
# panel "installed: true" diyordu, testler yesildi -- ama gorev Windows'un VARSAYILAN 72 saat
# calisma siniriyla kurulmustu ve posta her kurulumda uc gun sonra oluyordu. Hicbir birim testi
# bunu yakalayamazdi, cunku kirilma ZAMANLA geliyordu ve kurulum gunu her sey dogru gorunuyordu.
#
# Oncesinde ayni sinif iki kez daha yasandi: .htaccess modulu uc surum boyunca PAKETE HIC GIRMEDI
# (testler yesildi), ve FTP ev klasoru IIS'in aradigindan BASKA bir yola kuruluyordu (panelin
# kontrol ettigi her sey dogruydu).
#
# Bu betik, birim testlerinin goremedigi seyi olcer: KURULU MAKINEDEKI GERCEK DURUM.
# Hicbir sey degistirmez, yalnizca okur.
#
# KULLANIM (yonetici olarak):
#   powershell -File scripts\kurulum-dogrula.ps1
#   powershell -File scripts\kurulum-dogrula.ps1 -BeklenenSurum 0.8.4

param([string]$BeklenenSurum = "")

function Yaz([string]$m, [string]$renk = "Gray") { Write-Host $m -ForegroundColor $renk }
function Basarili([string]$m) { Yaz "  [TAMAM]     $m" "Green" }
function Uyari([string]$m)    { Yaz "  [DIKKAT]    $m" "Yellow" }
function Hata([string]$m)     { Yaz "  [BOZUK]     $m" "Red" }
function Olculemedi([string]$m) { Yaz "  [OLCULEMEDI] $m" "DarkGray" }

$bozuk = 0

Yaz "=== AdaPanel kurulum dogrulamasi ===" "Cyan"
Yaz ("Zaman: {0}" -f (Get-Date -Format "yyyy-MM-dd HH:mm"))
Yaz ""

# ---------------------------------------------------------------------------------------------
# 1) SURUM. Kurulan ile beklenen ayni mi.
# ---------------------------------------------------------------------------------------------
Yaz "1) Kurulu surum" "Cyan"
$exe = "C:\Program Files\AdaPanel\AdaPanel.Api.exe"
if (Test-Path $exe) {
    $bilgi = (Get-Item $exe).VersionInfo
    Yaz ("  exe        : {0}" -f $bilgi.ProductVersion)
    Yaz ("  CompanyName: {0}" -f $bilgi.CompanyName)

    # CompanyName kontrolu bir guvenlik olcumudur: bu gelistirme makinesinde PE dosyalarina
    # bulasan bir virus var ve bulasik dosyalar bu alani degistiriyor.
    #
    # ONEK ile karsilastirilir, ESITLIK ile DEGIL -- ve bu satiri betigin ILK KOSUSU yazdirdi:
    # kurulum paketinin CompanyName'i "AdaPanel", ama AdaPanel.Api.exe'ninki mesru olarak
    # "AdaPanel.Api" (derleme adindan gelir). Esitlik arayan ilk halim SAGLAM bir kurulumu
    # "bulasik olabilir" diye raporladi. Bir dogrulama aracinin yanlis alarmi, hicbir sey
    # olcmemesinden daha zararlidir: bir sonraki gercek alarm da gormezden gelinir.
    if (-not $bilgi.CompanyName.StartsWith("AdaPanel")) {
        Hata "CompanyName beklenmedik ('$($bilgi.CompanyName)') -- paket bulasik olabilir."
        $bozuk++
    } else { Basarili "CompanyName beklendigi gibi." }

    if ($BeklenenSurum -and $bilgi.ProductVersion -notlike "$BeklenenSurum*") {
        Hata "Beklenen $BeklenenSurum, kurulu $($bilgi.ProductVersion) -- kurulum uygulanmamis olabilir."
        $bozuk++
    } elseif ($BeklenenSurum) { Basarili "Surum beklenenle ayni." }
} else { Olculemedi "AdaPanel.Api.exe bulunamadi: $exe" }

# ---------------------------------------------------------------------------------------------
# 2) UZUN CALISAN GOREVLER -- 72 SAAT HATASININ TA KENDISI.
#    ExecutionTimeLimit PT0S olmali. PT72H geri geldiyse posta 3 gun sonra yine olur.
# ---------------------------------------------------------------------------------------------
Yaz ""
Yaz "2) Uzun calisan gorevler (72 saat siniri)" "Cyan"
foreach ($gorev in @("AdaStalwart", "AdaMemcached")) {
    try {
        $t = Get-ScheduledTask -TaskName $gorev -ErrorAction Stop
        $sinir = $t.Settings.ExecutionTimeLimit
        $pil   = $t.Settings.StopIfGoingOnBatteries
        $bilgi = Get-ScheduledTaskInfo -TaskName $gorev -ErrorAction SilentlyContinue

        Yaz ("  {0,-14} sinir={1}  pil={2}  durum={3}  sonSonuc={4}" -f `
             $gorev, $sinir, $pil, $t.State, $(if ($bilgi) { $bilgi.LastTaskResult } else { "?" }))

        if ($sinir -eq "PT0S" -or [string]::IsNullOrEmpty($sinir)) { Basarili "$gorev suresiz calisiyor." }
        else { Hata "$gorev calisma siniri $sinir -- 72 saat hatasi GERI GELMIS."; $bozuk++ }

        # 267014 = 0x41306 = SCHED_S_TASK_TERMINATED: gorev Windows tarafindan durdurulmus.
        if ($bilgi -and $bilgi.LastTaskResult -eq 267014) { Hata "$gorev en son SONLANDIRILMIS (0x41306)."; $bozuk++ }
    } catch { Olculemedi "$gorev okunamadi: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------------------------------------
# 3) POSTA GERCEKTEN DINLIYOR MU. Panelin "installed: true" demesi yetmez -- o cevap 72 saat
#    hatasi sirasinda da "true" geliyordu.
# ---------------------------------------------------------------------------------------------
Yaz ""
Yaz "3) Posta portlari (gercekten dinleniyor mu)" "Cyan"
$acik = 0
foreach ($p in 25, 143, 465, 587, 993) {
    $var = (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue) -ne $null
    if ($var) { $acik++; Yaz ("  {0,5} dinleniyor" -f $p) } else { Yaz ("  {0,5} kapali" -f $p) "DarkGray" }
}
if ($acik -gt 0) { Basarili "$acik posta portu dinleniyor." }
else { Uyari "Hicbir posta portu dinlenmiyor -- posta kurulu degilse NORMAL, kuruluysa BOZUK." }

# ---------------------------------------------------------------------------------------------
# 4) FTP EV KLASORLERI -- IIS'in HANGI yolu aradigi makineye gore degisiyor, o yuzden panel
#    IKISINI BIRDEN kurar. Biri eksikse o kullanici giris yapamaz.
# ---------------------------------------------------------------------------------------------
Yaz ""
Yaz "4) FTP ev klasorleri (iki yol da kurulu mu)" "Cyan"
$ftpKok = "C:\ProgramData\AdaPanel\ftp-root"
$yerel   = Join-Path $ftpKok "LocalUser"
$makine  = Join-Path $ftpKok $env:COMPUTERNAME
if (Test-Path $yerel) {
    $kullanicilar = @(Get-ChildItem $yerel -Force -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    if ($kullanicilar.Count -eq 0) { Olculemedi "FTP kullanicisi yok; olcecek bir sey yok." }
    foreach ($k in $kullanicilar) {
        $ikinci = Join-Path $makine $k
        if (Test-Path $ikinci) { Basarili "$k : iki yol da var." }
        else { Uyari "$k : makine adli yol YOK ($ikinci). Bu kullanici 0.8.4 oncesinde acilmis olabilir -- panelden parolasini yenilemek iki yolu da kurar." }
    }
} else { Olculemedi "FTP kok klasoru yok: $yerel" }

# ---------------------------------------------------------------------------------------------
# 5) PANEL AYAKTA MI. Kurulum "basarili" deyip servisi baslatamamis olabilir.
# ---------------------------------------------------------------------------------------------
Yaz ""
Yaz "5) Panel servisi" "Cyan"
try {
    $s = Get-Service AdaPanel -ErrorAction Stop
    if ($s.Status -eq "Running") { Basarili "AdaPanel servisi calisiyor." }
    else { Hata "AdaPanel servisi $($s.Status)."; $bozuk++ }
} catch { Hata "AdaPanel servisi bulunamadi."; $bozuk++ }

# ---------------------------------------------------------------------------------------------
Yaz ""
Yaz "=== SONUC ===" "Cyan"
if ($bozuk -eq 0) { Yaz "Kesin bozuk bulgu YOK." "Green" }
else { Yaz "$bozuk kesin bozuk bulgu var (yukarida [BOZUK])." "Red" }
Yaz "[DIKKAT] ve [OLCULEMEDI] satirlari hata DEGILDIR; olculemeyeni bozuk saymak, bu depoda" "DarkGray"
Yaz "kapatilan hata sinifinin ters yonudur." "DarkGray"
exit $(if ($bozuk -eq 0) { 0 } else { 1 })
