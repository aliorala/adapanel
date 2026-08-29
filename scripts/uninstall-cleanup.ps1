param(
    [string]$InstallDir = "C:\Program Files\AdaPanel",
    [string]$DataRoot = "$env:ProgramData\AdaPanel",
    # "1" = verileri KORU (varsayilan guvenli mod): yalnizca panel servisi + self-update gorevi kaldirilir;
    # musteri veritabanlari, IIS siteleri, yedekler, DB servisleri CALISMAYA DEVAM eder (Plesk davranisi).
    # "0" = tam temizlik (asagidaki eski davranis, kullanici kaldirirken ACIKCA onaylarsa).
    [string]$KeepData = "1",
    # Yalnizca testler icin: fonksiyonlari tanimla, HICBIR SEY calistirma.
    # (Bu betigin ana akisi servis siler ve klasor siler; test asla ana akisi kosturmamali.)
    [switch]$DefineFunctionsOnly
)

# SESSIZLIK DARALTILDI.
#
# Onceden butun betik "$ErrorActionPreference = 'SilentlyContinue'" ile kosuyordu ve cikis kodu
# HER ZAMAN 0'di. Sonuc: bir adim hic calismasa bile kimse gormuyordu -- FTP hesabi temizligi tam
# olarak boyle olu kaldi (kayit dosyasinin yolunda bir ters bolu eksikti, dosya HICBIR ZAMAN
# bulunmuyordu, fonksiyon her seferinde "kayit yok" deyip donuyordu).
#
# Artik: varsayilan "Stop" ve her adim kendi try/catch'inde OLCULUYOR; sonuclar rapora yaziliyor.
$ErrorActionPreference = "Stop"

$script:RaporSatirlari = [System.Collections.Generic.List[string]]::new()
$script:BasarisizAdimSayisi = 0

function Add-AdaRapor {
    param([string] $Satir)
    $script:RaporSatirlari.Add($Satir) | Out-Null
    Write-Host $Satir
}

function Invoke-AdaAdim {
    <#
        Tek bir kaldirma adimini calistirir, SONUCUNU OLCER ve rapora yazar.
        Bir adimin patlamasi digerlerini engellemez -- ama artik sessizce yutulmaz.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Ad,
        [Parameter(Mandatory = $true)][scriptblock] $Islem,
        # EN IYI CABA: adim basarisiz olabilir ve bu bir SORUN DEGILDIR (ornegin makinede IIS yok).
        # Rapora yine yazilir -- ama cikis kodunu bozmaz. Kritik adimlar bu bayragi TASIMAZ.
        [switch] $EnIyiCaba
    )

    try {
        $sonuc = & $Islem
        if ($null -ne $sonuc -and "$sonuc".Trim().Length -gt 0) {
            Add-AdaRapor ("  [TAMAM] {0}: {1}" -f $Ad, ("$sonuc" -join "; "))
        } else {
            Add-AdaRapor ("  [TAMAM] {0}" -f $Ad)
        }
        return $true
    }
    catch {
        if ($EnIyiCaba) {
            Add-AdaRapor ("  [ATLANDI] {0}: {1}" -f $Ad, $_.Exception.Message)
        } else {
            $script:BasarisizAdimSayisi++
            Add-AdaRapor ("  [HATA ] {0}: {1}" -f $Ad, $_.Exception.Message)
        }
        return $false
    }
}

# --- FTP HESAPLARI ---------------------------------------------------------------------------
#
# FTP KULLANICILARI GERCEK WINDOWS HESABIDIR. Panel her FTP hesabi icin "net user <ad> <parola>
# /add" ile bir YEREL WINDOWS HESABI acar. Panel kaldirildiktan sonra bu hesaplar sunucuda kalirsa,
# parolalari musterilerin elinde olan N adet yerel hesap acikta demektir.
#
# HESAPLAR TAHMINLE BULUNMAZ. Ad desenine bakip silmek (ornegin "ftp*") panelin acmadigi bir hesabi
# silebilirdi ve yerel hesap silmek geri alinamaz. Bu yuzden yalnizca PANELIN KENDI KAYDINDA yazan
# adlar islenir.

function Get-AdaFtpKayitYolu {
    <#
        Kayit dosyasinin yolu. Bu yol TEK KARAKTER yuzunden aylarca yanlisti:
            Join-Path $DataRoot "configtp-users.json"     <-- ters bolu kaybolmus
        Dogrusu "config\ftp-users.json"; kaynak: FtpService.StatePath = "config/ftp-users.json"
        (BackupService de tam-sunucu geri yuklemede ayni yolu kullaniyor).
    #>
    param([Parameter(Mandatory = $true)][string] $DataRoot)

    return (Join-Path $DataRoot "config\ftp-users.json")
}

function Get-AdaFtpAccounts {
    <#
        Panelin kaydindaki FTP hesap adlari + o hesabin Windows'ta HALA VAR olup olmadigi.
        Kayit yoksa bos dizi doner; kayit BOZUKSA istisna atar (sessizce "hesap yok" demez --
        eski davranis tam olarak buydu ve hatayi gizliyordu).
    #>
    param([Parameter(Mandatory = $true)][string] $DataRoot)

    $kayit = Get-AdaFtpKayitYolu -DataRoot $DataRoot
    if (-not (Test-Path -LiteralPath $kayit)) {
        return @()
    }

    $kullanicilar = Get-Content -LiteralPath $kayit -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if (-not $kullanicilar) { return @() }

    $sonuc = [System.Collections.Generic.List[object]]::new()
    foreach ($kullanici in @($kullanicilar)) {
        $ad = "$($kullanici.UserName)".Trim()
        if ([string]::IsNullOrWhiteSpace($ad)) { continue }

        $var = $false
        try { $var = [bool](Get-LocalUser -Name $ad -ErrorAction Stop) } catch { $var = $false }

        $sonuc.Add([pscustomobject]@{ UserName = $ad; Exists = $var }) | Out-Null
    }

    return $sonuc.ToArray()
}

function Remove-AdaFtpAccounts {
    <#
        -ListOnly: hesaplari SILMEZ, yalnizca dondurur. Veri koruma modunda (KeepData=1) siteler
        calismaya devam ettigi icin FTP erisimi de KASITLI olarak birakilir -- ama SESSIZ
        birakilmaz: kalan hesaplar kaldirma raporunda ACIKCA listelenir.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $DataRoot,
        [switch] $ListOnly
    )

    $hesaplar = Get-AdaFtpAccounts -DataRoot $DataRoot
    if ($hesaplar.Count -eq 0) {
        return @()
    }

    $islenen = [System.Collections.Generic.List[object]]::new()
    foreach ($hesap in $hesaplar) {
        if (-not $hesap.Exists) {
            $islenen.Add([pscustomobject]@{ UserName = $hesap.UserName; Durum = "zaten yok" }) | Out-Null
            continue
        }

        if ($ListOnly) {
            $islenen.Add([pscustomobject]@{ UserName = $hesap.UserName; Durum = "KALDI (veri koruma modu)" }) | Out-Null
            continue
        }

        try {
            Remove-LocalUser -Name $hesap.UserName -ErrorAction Stop
            $islenen.Add([pscustomobject]@{ UserName = $hesap.UserName; Durum = "silindi" }) | Out-Null
        }
        catch {
            $islenen.Add([pscustomobject]@{ UserName = $hesap.UserName; Durum = "SILINEMEDI: $($_.Exception.Message)" }) | Out-Null
        }
    }

    return $islenen.ToArray()
}

# --- KLASOR BAGLANTILARI ---------------------------------------------------------------------
#
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
    <#
        Basarisiz olan her nokta RAPORLANIR ve cagirana dondurulur.

        ONCEKI HALI TEHLIKELIYDI: EnumerateDirectories hata verince "continue" deniyordu, yani o
        alt agactaki junction'lar KALDIRILMADAN kaliyordu -- ve hemen ardindan gelen
        "Remove-Item -Recurse" onlari TAKIP EDIYORDU. Erisim reddedilen tek bir alt klasor, tam da
        onlenmeye calisilan musteri-dosyasi kaybini geri getiriyordu. Artik basarisizlik
        YUTULMUYOR; cagiran, agaci silmeyi REDDEDIYOR.

        -Enumerator: alt klasor listeleyiciyi degistirmeye yarar. Varsayilani GERCEK cagridir;
        testler "erisim reddedildi" durumunu bu makinede guvenilir sekilde uretemedigi icin
        (yonetici olarak kosan surec ACL'i asabiliyor) hata dali ancak boyle dogrulanabiliyor.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [scriptblock] $Enumerator
    )

    $basarisiz = [System.Collections.Generic.List[string]]::new()
    $kaldirilan = [System.Collections.Generic.List[string]]::new()

    if (-not [System.IO.Directory]::Exists($Root)) {
        return [pscustomobject]@{ Kaldirilan = @(); Basarisiz = @() }
    }

    $bekleyen = New-Object 'System.Collections.Generic.Stack[string]'
    $bekleyen.Push($Root)

    while ($bekleyen.Count -gt 0) {
        $simdiki = $bekleyen.Pop()

        try {
            $altKlasorler = if ($Enumerator) { & $Enumerator $simdiki } else { [System.IO.Directory]::EnumerateDirectories($simdiki) }
        } catch {
            # ARTIK YUTULMUYOR: listelenemeyen bir alt agac, icinde kaldirilmamis bir baglanti
            # BULUNABILECEGI anlamina gelir. Cagiran bu agaci silmeyi reddetmeli.
            $basarisiz.Add("$simdiki -- listelenemedi: $($_.Exception.Message)") | Out-Null
            continue
        }

        foreach ($alt in $altKlasorler) {
            try {
                $nitelikler = [System.IO.File]::GetAttributes($alt)
            } catch {
                $basarisiz.Add("$alt -- nitelikleri okunamadi: $($_.Exception.Message)") | Out-Null
                continue
            }

            if ($nitelikler -band [System.IO.FileAttributes]::ReparsePoint) {
                # Yalnizca baglantiyi kaldirir; hedefteki dosyalara DOKUNMAZ.
                try {
                    [System.IO.Directory]::Delete($alt)
                    $kaldirilan.Add($alt) | Out-Null
                } catch {
                    $basarisiz.Add("$alt -- baglanti kaldirilamadi: $($_.Exception.Message)") | Out-Null
                }
            } else {
                $bekleyen.Push($alt)
            }
        }
    }

    return [pscustomobject]@{ Kaldirilan = $kaldirilan.ToArray(); Basarisiz = $basarisiz.ToArray() }
}

# --- KALAN IZLER -----------------------------------------------------------------------------

function Get-AdaKalanFirewallKurallari {
    try {
        return @(Get-NetFirewallRule -DisplayName "AdaPanel*" -ErrorAction Stop | Select-Object -ExpandProperty DisplayName)
    } catch {
        return @()
    }
}

function Get-AdaKalanSertifikalar {
    $bulunan = [System.Collections.Generic.List[string]]::new()
    foreach ($depo in @("Cert:\LocalMachine\My", "Cert:\LocalMachine\Root", "Cert:\CurrentUser\My", "Cert:\CurrentUser\Root")) {
        try {
            Get-ChildItem $depo -ErrorAction Stop |
                Where-Object { $_.FriendlyName -eq "AdaPanel Self Signed" } |
                ForEach-Object { $bulunan.Add("$depo\$($_.Thumbprint)") | Out-Null }
        } catch {
            # Depo okunamadi; rapora dusmez ama sessiz de degil.
            $bulunan.Add("$depo -- okunamadi") | Out-Null
        }
    }
    return $bulunan.ToArray()
}

function Get-AdaRaporYolu {
    <#
        Rapor KULLANICININ GOREBILECEGI bir yere yazilir.
        Veri koku duruyorsa oraya; tam temizlikte silindigi icin bir ust klasore.
    #>
    param([Parameter(Mandatory = $true)][string] $DataRoot)

    if (Test-Path -LiteralPath $DataRoot) {
        return (Join-Path $DataRoot "kaldirma-raporu.txt")
    }

    $ust = Split-Path $DataRoot -Parent
    if ([string]::IsNullOrWhiteSpace($ust)) { $ust = $env:ProgramData }
    return (Join-Path $ust "AdaPanel-kaldirma-raporu.txt")
}

function Write-AdaKaldirmaRaporu {
    param(
        [Parameter(Mandatory = $true)][string] $Yol,
        # AllowEmptyString ZORUNLU: raporda bilerek bos ayirici satirlar var ve Mandatory bir
        # [string[]] parametresi bos dizge iceren diziyi reddediyor ("Cannot bind argument ...
        # because it is an empty string").
        [Parameter(Mandatory = $true)][AllowEmptyString()][AllowEmptyCollection()][string[]] $Satirlar
    )

    $govde = @(
        "AdaPanel kaldirma raporu",
        "Tarih: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        ""
    ) + $Satirlar

    $klasor = Split-Path $Yol -Parent
    if ($klasor -and -not (Test-Path -LiteralPath $klasor)) {
        New-Item -ItemType Directory -Path $klasor -Force | Out-Null
    }

    Set-Content -LiteralPath $Yol -Value $govde -Encoding utf8
    return $Yol
}

function New-AdaKoruModuRaporu {
    <#
        VERI KORUMA MODUNUN RAPORU. Bu mod VARSAYILANDIR ve hicbir sey silmez -- ama sunucuda
        NELERIN KALDIGINI kullaniciya soylemek zorundadir. Ozellikle FTP hesaplari: bunlar gercek
        yerel Windows hesabidir ve parolalari musterilerdedir.

        Ayri bir fonksiyon olmasinin sebebi test edilebilirligidir: ana akis servis siler ve klasor
        siler, dolayisiyla bir test onu ASLA kosturamaz.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $DataRoot,
        [AllowEmptyCollection()][object[]] $Hesaplar = @(),
        [AllowEmptyString()][AllowEmptyCollection()][string[]] $FirewallKurallari = @(),
        [AllowEmptyString()][AllowEmptyCollection()][string[]] $Sertifikalar = @(),
        [AllowEmptyString()][AllowEmptyCollection()][string[]] $Adimlar = @()
    )

    $satirlar = [System.Collections.Generic.List[string]]::new()
    $satirlar.Add("MOD: veri koruma (varsayilan). Panel kaldirildi; siteler, veritabanlari ve yedekler DURUYOR.") | Out-Null
    $satirlar.Add("") | Out-Null
    $satirlar.Add("Adimlar:") | Out-Null
    foreach ($adim in $Adimlar) { $satirlar.Add($adim) | Out-Null }
    $satirlar.Add("") | Out-Null
    $satirlar.Add("SUNUCUDA KALAN IZLER -- bunlar BILEREK silinmedi, ama silinmedikleri icin bilmelisiniz:") | Out-Null
    $satirlar.Add("") | Out-Null

    $satirlar.Add("1) FTP HESAPLARI (GERCEK YEREL WINDOWS HESABI, parolalari kullanicilarinda):") | Out-Null
    if ($Hesaplar.Count -eq 0) {
        $satirlar.Add("   - kayitli hesap yok.") | Out-Null
    } else {
        foreach ($hesap in $Hesaplar) {
            $satirlar.Add("   - $($hesap.UserName)  [$($hesap.Durum)]") | Out-Null
        }
        $satirlar.Add("   Bu hesaplar siteler ayakta kalsin diye BIRAKILDI. Artik gerekmiyorsa:") | Out-Null
        $satirlar.Add("       Remove-LocalUser -Name <ad>") | Out-Null
    }

    $satirlar.Add("") | Out-Null
    $satirlar.Add("2) GUVENLIK DUVARI KURALLARI:") | Out-Null
    if ($FirewallKurallari.Count -eq 0) {
        $satirlar.Add("   - kalan kural yok.") | Out-Null
    } else {
        foreach ($kural in $FirewallKurallari) { $satirlar.Add("   - $kural") | Out-Null }
        $satirlar.Add("   Kaldirmak icin: Get-NetFirewallRule -DisplayName 'AdaPanel*' | Remove-NetFirewallRule") | Out-Null
    }

    $satirlar.Add("") | Out-Null
    $satirlar.Add("3) PANEL SERTIFIKASI ('AdaPanel Self Signed'):") | Out-Null
    if ($Sertifikalar.Count -eq 0) {
        $satirlar.Add("   - kalan sertifika yok.") | Out-Null
    } else {
        foreach ($sertifika in $Sertifikalar) { $satirlar.Add("   - $sertifika") | Out-Null }
    }

    $satirlar.Add("") | Out-Null
    $satirlar.Add("4) VERI KOKU: $DataRoot (siteler/veritabanlari/yedekler burada, KORUNDU)") | Out-Null

    return $satirlar.ToArray()
}

function Remove-AdaAgacGuvenli {
    <#
        Bir klasor agacini ANCAK icindeki butun klasor baglantilari cozulebildiyse siler.

        Cozulemeyen tek bir baglanti bile kalmissa SILMEYI REDDEDER: "Remove-Item -Recurse"
        Windows PowerShell 5.1'de baglantiyi TAKIP EDEBILIR ve hedefteki musteri site dosyalarini
        goturebilir. Klasoru birakip kullaniciya soylemek, musterinin sitesini silmekten HER ZAMAN
        iyidir.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [scriptblock] $Enumerator
    )

    # Yerel takma ad: bu betikteki silme cagrisinin sekli ReparsePointSafetyTests tarafindan
    # metin olarak sabitlenmis durumda (bilerek: bu makinede tehlikeli davranis uretilemiyor,
    # koruyucu dalin VARLIGINI dogrulayan tek sey o denetim).
    $expandedPath = $Root

    $baglantiSonucu = Remove-AdaReparsePoints -Root $expandedPath -Enumerator $Enumerator
    if ($baglantiSonucu.Basarisiz.Count -gt 0) {
        return [pscustomobject]@{
            Silindi   = $false
            Basarisiz = $baglantiSonucu.Basarisiz
            Hata      = $null
        }
    }

    try {
        Remove-Item -LiteralPath $expandedPath -Recurse -Force -ErrorAction Stop
        return [pscustomobject]@{ Silindi = $true; Basarisiz = @(); Hata = $null }
    }
    catch {
        return [pscustomobject]@{ Silindi = $false; Basarisiz = @(); Hata = $_.Exception.Message }
    }
}

if ($DefineFunctionsOnly) { return }

# =============================================================================================
# ANA AKIS
# =============================================================================================

Add-AdaRapor "AdaPanel kaldiriliyor (KeepData=$KeepData)."

# --- VERI KORUMA MODU (VARSAYILAN) -----------------------------------------------------------
#
# BU DAL VARSAYILANDIR ve sessiz kaldirma da bunu kullanir. Onceden bu dal "exit 0" ile cikiyordu;
# FTP hesabi temizligi ise cikistan ~260 satir SONRA cagriliyordu -- yani varsayilan kaldirma
# yolunda o koda HIC ULASILMIYORDU. Artik hesaplar bu dalda da ISLENIYOR: silinmiyorlar (siteler
# ayakta kaldigi icin FTP erisimi kasitli) ama RAPORLANIYORLAR.
if ($KeepData -eq "1") {
    Invoke-AdaAdim "Panel servisi durduruldu ve kaldirildi" {
        $panelService = Get-Service -Name "AdaPanel" -ErrorAction SilentlyContinue
        if (-not $panelService) { return "servis zaten yok" }

        Stop-Service -Name "AdaPanel" -Force -ErrorAction SilentlyContinue
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Milliseconds 500
            $panelService = Get-Service -Name "AdaPanel" -ErrorAction SilentlyContinue
            if (-not $panelService -or $panelService.Status -eq "Stopped") { break }
        }

        & sc.exe delete "AdaPanel" | Out-Null
        if ((Get-Service -Name "AdaPanel" -ErrorAction SilentlyContinue)) {
            throw "servis kaydi silinemedi (sc.exe delete sonrasi hala kayitli)"
        }
        return "silindi"
    } | Out-Null

    Invoke-AdaAdim "Self-update gorevi kaldirildi" {
        Stop-ScheduledTask -TaskName "AdaPanel-SelfUpdate" -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName "AdaPanel-SelfUpdate" -Confirm:$false -ErrorAction SilentlyContinue
        if (Get-ScheduledTask -TaskName "AdaPanel-SelfUpdate" -ErrorAction SilentlyContinue) {
            throw "gorev hala kayitli"
        }
        return "kaldirildi"
    } | Out-Null

    $kalanHesaplar = @()
    Invoke-AdaAdim "FTP hesaplari incelendi" {
        $script:kalanHesaplar = Remove-AdaFtpAccounts -DataRoot $DataRoot -ListOnly
        return ("{0} kayitli hesap" -f $script:kalanHesaplar.Count)
    } | Out-Null
    $kalanHesaplar = $script:kalanHesaplar

    $kalanKurallar = Get-AdaKalanFirewallKurallari
    $kalanSertifikalar = Get-AdaKalanSertifikalar

    $satirlar = New-AdaKoruModuRaporu `
        -DataRoot $DataRoot `
        -Hesaplar $kalanHesaplar `
        -FirewallKurallari $kalanKurallar `
        -Sertifikalar $kalanSertifikalar `
        -Adimlar $script:RaporSatirlari.ToArray()

    $raporYolu = Write-AdaKaldirmaRaporu -Yol (Get-AdaRaporYolu -DataRoot $DataRoot) -Satirlar $satirlar
    Write-Host "AdaPanel removed. Hosted sites, databases, and data were preserved."
    Write-Host "Kaldirma raporu: $raporYolu"

    exit $(if ($script:BasarisizAdimSayisi -gt 0) { 1 } else { 0 })
}

# --- TAM TEMIZLIK ----------------------------------------------------------------------------

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
    Invoke-AdaAdim "Servis kaldirildi: $serviceName" {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if (-not $service) { return "yok" }

        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Milliseconds 500
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if (-not $service -or $service.Status -eq "Stopped") { break }
        }

        & sc.exe delete $serviceName | Out-Null
        return "silindi"
    } | Out-Null
}

# DIKKAT: buradaki adlar GOREV AGACININ KOKUNDE duran gorevlerdir. Asagidaki "\AdaPanel\" klasor
# supurmesi onlari YAKALAMAZ -- iki ayri temizlik, iki ayri yer.
#
# "AdaPanel Tepsi" OLCUM SONUCU EKLENDI: install-tray-task.ps1 gorevi `schtasks /Create /TN
# 'AdaPanel Tepsi'` ile KLASORSUZ, yani kokte olusturuyor. Bu listede yoktu ve "\AdaPanel\"
# supurmesine de takilmiyordu; kurulu bir makinede olculdu, gorev kaldirmadan SONRA da Windows'ta
# kaliyordu. Sonucu gorunur bir sey degil ama zararsiz da degil: gorev her birkac dakikada bir
# SILINMIS {app}\AdaPanel.Api.exe'yi calistirmayi deniyor, yani kaldirilan urun makinede kalici
# bir hata kaydi uretmeye devam ediyordu.
#
# Ad DEGISTIRILIRSE burasi sessizce kayar; bu yuzden install-tray-task.ps1 ile bu liste arasindaki
# tutarlilik UninstallCleanupScriptTests icinde ayrica sinaniyor.
$taskNames = @(
    "AdaPanel Nginx",
    "AdaStalwart",
    "AdaMemcached",
    "AdaPanel-SelfUpdate",
    "AdaPanel Tepsi"
)
foreach ($taskName in $taskNames) {
    Invoke-AdaAdim "Zamanlanmis gorev kaldirildi: $taskName" {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        return "islendi"
    } -EnIyiCaba | Out-Null
}

Invoke-AdaAdim "AdaPanel gorev klasoru temizlendi" {
    Get-ScheduledTask -TaskPath "\AdaPanel\" -ErrorAction SilentlyContinue |
        ForEach-Object {
            Stop-ScheduledTask -TaskPath $_.TaskPath -TaskName $_.TaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskPath $_.TaskPath -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
        }
    return "islendi"
} -EnIyiCaba | Out-Null

$winAcmeRoot = Join-Path $DataRoot "tools\win-acme"
if (Test-Path -LiteralPath $winAcmeRoot) {
    Invoke-AdaAdim "win-acme gorevleri kaldirildi" {
        Get-ScheduledTask -ErrorAction SilentlyContinue |
            Where-Object { $_.TaskName -like "win-acme*" -or $_.TaskPath -like "*win-acme*" } |
            ForEach-Object {
                Stop-ScheduledTask -TaskPath $_.TaskPath -TaskName $_.TaskName -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskPath $_.TaskPath -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
            }
        return "islendi"
    } -EnIyiCaba | Out-Null
}

# AdaPanel'in OLUSTURDUGU tum IIS sitelerini (kullanici domainleri dahil) kaldir. AdaPanel
# siteleri her zaman 'AdaPanel.*' app pool'u kullanir; bu kesin bir isarettir. Boylece panel
# kaldirilinca Windows'ta artik site/domain kaydi kalmaz (Plesk gibi temiz kaldirma).
Invoke-AdaAdim "IIS siteleri ve app pool'lari kaldirildi (WebAdministration)" {
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
    return "islendi"
} -EnIyiCaba | Out-Null

$appCmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
if (Test-Path -LiteralPath $appCmd) {
    Invoke-AdaAdim "IIS siteleri appcmd yedegiyle kaldirildi" {
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
        return "islendi"
    } -EnIyiCaba | Out-Null
}

function Invoke-ManagedUninstaller {
    param(
        [string]$Executable,
        [string]$Arguments
    )

    if (-not (Test-Path -LiteralPath $Executable)) {
        return "yok: $Executable"
    }

    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru -ErrorAction Stop
    if ($process -and $process.ExitCode -notin @(0, 1605, 1641, 3010)) {
        throw "Uninstaller failed: $Executable (exit $($process.ExitCode))"
    }

    return "calisti: $([System.IO.Path]::GetFileName($Executable))"
}

$downloadsRoot = Join-Path $DataRoot "downloads"
$postgresRoot = Join-Path $DataRoot "runtimes\postgresql\16"
$postgresUninstaller = Get-ChildItem -LiteralPath $postgresRoot -Filter "uninstall-postgresql*.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($postgresUninstaller) {
    Invoke-AdaAdim "PostgreSQL kaldirildi" { Invoke-ManagedUninstaller $postgresUninstaller.FullName "--mode unattended" } -EnIyiCaba | Out-Null
}

$pgAdminRoot = Join-Path $DataRoot "tools\pgadmin4"
$pgAdminUninstaller = Get-ChildItem -LiteralPath $pgAdminRoot -Filter "unins*.exe" -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    Select-Object -First 1
if ($pgAdminUninstaller) {
    Invoke-AdaAdim "pgAdmin kaldirildi" { Invoke-ManagedUninstaller $pgAdminUninstaller.FullName "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" } -EnIyiCaba | Out-Null
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
        Invoke-AdaAdim "MSI kaldirildi: $($item.Marker)" {
            Invoke-ManagedUninstaller "msiexec.exe" "/x `"$($package.FullName)`" /qn /norestart"
        } -EnIyiCaba | Out-Null
    }
}

if (Test-Path -LiteralPath (Join-Path $DataRoot "runtimes\python\3")) {
    $pythonInstaller = Get-ChildItem -LiteralPath $downloadsRoot -Filter "python-*-amd64.exe" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($pythonInstaller) {
        Invoke-AdaAdim "Python kaldirildi" { Invoke-ManagedUninstaller $pythonInstaller.FullName "/uninstall /quiet" } -EnIyiCaba | Out-Null
    }
}

Invoke-AdaAdim "AdaPanel'in kendi surecleri durduruldu" {
    $nginxRoot = Join-Path $DataRoot "runtimes\nginx"
    $resolvedNginxRoot = [System.IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables($nginxRoot)
    ).TrimEnd('\') + '\'

    $resolvedDataRoot = [System.IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables($DataRoot)
    ).TrimEnd('\') + '\'

    $durdurulan = 0
    Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $processPath = $_.Path
            if ([string]::IsNullOrWhiteSpace($processPath)) { return }

            $resolvedProcessPath = [System.IO.Path]::GetFullPath($processPath)
            if ($resolvedProcessPath.StartsWith($resolvedDataRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
                $resolvedProcessPath.StartsWith($resolvedNginxRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                $durdurulan++
            }
        }
        catch {
        }
    }

    return "$durdurulan surec"
} -EnIyiCaba | Out-Null

Invoke-AdaAdim "Guvenlik duvari kurallari kaldirildi" {
    Get-NetFirewallRule -DisplayName "AdaPanel*" -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue

    $kalan = Get-AdaKalanFirewallKurallari
    if ($kalan.Count -gt 0) { throw "kalan kural: $($kalan -join ', ')" }
    return "temiz"
} | Out-Null

Invoke-AdaAdim "Panel sertifikasi kaldirildi" {
    foreach ($certificatePath in @("Cert:\LocalMachine\My", "Cert:\LocalMachine\Root", "Cert:\CurrentUser\My", "Cert:\CurrentUser\Root")) {
        Get-ChildItem $certificatePath -ErrorAction SilentlyContinue |
            Where-Object { $_.FriendlyName -eq "AdaPanel Self Signed" } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    $kalan = Get-AdaKalanSertifikalar
    if ($kalan.Count -gt 0) { throw "kalan sertifika: $($kalan -join ', ')" }
    return "temiz"
} | Out-Null

# Hesaplar veri koku SILINMEDEN once temizlenir: kayit dosyasi veri kokunun altinda duruyor.
$silinenHesaplar = @()
Invoke-AdaAdim "FTP hesaplari silindi" {
    $script:silinenHesaplar = Remove-AdaFtpAccounts -DataRoot $DataRoot
    if ($script:silinenHesaplar.Count -eq 0) { return "kayitli hesap yok" }

    $sorunlu = @($script:silinenHesaplar | Where-Object { $_.Durum -like "SILINEMEDI*" })
    if ($sorunlu.Count -gt 0) {
        throw ("silinemeyen hesap: " + (($sorunlu | ForEach-Object { $_.UserName }) -join ", "))
    }
    return ("{0} hesap islendi" -f $script:silinenHesaplar.Count)
} | Out-Null
$silinenHesaplar = $script:silinenHesaplar

$reddedilenAgaclar = [System.Collections.Generic.List[string]]::new()
foreach ($path in @($DataRoot, $InstallDir)) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        continue
    }

    $expandedPath = [Environment]::ExpandEnvironmentVariables($path)
    if (-not (Test-Path -LiteralPath $expandedPath)) {
        continue
    }

    $silmeSonucu = Remove-AdaAgacGuvenli -Root $expandedPath

    if ($silmeSonucu.Basarisiz.Count -gt 0) {
        # SILME REDDEDILDI. Kaldirilamayan bir reparse point kalmissa "Remove-Item -Recurse" onu
        # TAKIP EDEBILIR ve hedefteki musteri dosyalarini goturebilir. Klasoru birakip kullaniciya
        # soylemek, sitesini silmekten HER ZAMAN iyidir.
        $script:BasarisizAdimSayisi++
        Add-AdaRapor ("  [REDDEDILDI] {0} SILINMEDI -- icinde cozulemeyen klasor baglantisi olabilir:" -f $expandedPath)
        foreach ($sorun in $silmeSonucu.Basarisiz) {
            Add-AdaRapor "      - $sorun"
            $reddedilenAgaclar.Add("$expandedPath -> $sorun") | Out-Null
        }
        continue
    }

    if ($silmeSonucu.Silindi) {
        Add-AdaRapor ("  [TAMAM] Klasor silindi: {0}" -f $expandedPath)
    } else {
        $script:BasarisizAdimSayisi++
        Add-AdaRapor ("  [HATA ] Klasor silinemedi: {0}: {1}" -f $expandedPath, $silmeSonucu.Hata)
    }
}

$satirlar = [System.Collections.Generic.List[string]]::new()
$satirlar.Add("MOD: tam temizlik (kullanici ACIKCA onayladi). Veriler, siteler ve veritabanlari silindi.")
$satirlar.Add("")
$satirlar.Add("Adimlar:")
foreach ($satir in $script:RaporSatirlari) { $satirlar.Add($satir) }
$satirlar.Add("")
$satirlar.Add("FTP HESAPLARI:")
if ($silinenHesaplar.Count -eq 0) {
    $satirlar.Add("   - kayitli hesap yok.")
} else {
    foreach ($hesap in $silinenHesaplar) { $satirlar.Add("   - $($hesap.UserName)  [$($hesap.Durum)]") }
}
if ($reddedilenAgaclar.Count -gt 0) {
    $satirlar.Add("")
    $satirlar.Add("SILINMESI REDDEDILEN KLASORLER (elle bakin -- icinde klasor baglantisi olabilir):")
    foreach ($agac in $reddedilenAgaclar) { $satirlar.Add("   - $agac") }
}
$satirlar.Add("")
$satirlar.Add("Basarisiz adim sayisi: $script:BasarisizAdimSayisi")

$raporYolu = Write-AdaKaldirmaRaporu -Yol (Get-AdaRaporYolu -DataRoot $DataRoot) -Satirlar $satirlar.ToArray()
Write-Host "AdaPanel uninstall cleanup completed."
Write-Host "Kaldirma raporu: $raporYolu"

exit $(if ($script:BasarisizAdimSayisi -gt 0) { 1 } else { 0 })
