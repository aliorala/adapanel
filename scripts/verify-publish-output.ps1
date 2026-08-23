# PAKETLENECEK ICERIGIN DENETIMI -- "ne uretildi" degil, "ne PAKETLENIYOR" sorusunu cevaplar.
#
# NEDEN VAR: package-installer.ps1 yalnizca uretilen exe'nin boyutunu ve SHA256'sini YAZDIRIYORDU.
# Bu bir KAYITTIR, dogrulama degil. Bu makinede bir EXE bulastiricisi (Synaptics) uretilen kurulum
# paketlerini bulastirmisti ve "yayindaki dosyayi indir, yereldekiyle hash karsilastir" yontemi bu
# sinifa TAMAMEN KORDUR: yereldeki dosya zaten bulasikti, iki hash de birebir tutuyordu.
#
# Hash karsilastirmasi "aktarim sirasinda bozulma" yakalar. "Kaynakta bulasma" yakalamaz.
# Yakalayan sey: paketin ICINDE NE OLDUGUNU beklenen listeyle karsilastirmaktir.
#
# Bu betik:
#   1) publish klasorundeki HER dosyanin yolunu + SHA256'sini manifest'e yazar,
#   2) beklenmeyen bir CALISTIRILABILIR (PE) dosya varsa DURDURUR,
#   3) her PE dosyasinin imzasini ve CompanyName'ini denetler; bulasma imzasi ya da
#      beklenmedik bir yayinci varsa DURDURUR.
#
# Uzantiya DEGIL, dosyanin ICERIGINE bakar: bulastirici yuku ".jpg" adiyla birakabilir.

#
# IKI AYRI SORU, IKI AYRI ADIM:
#   -PublishDir     : ISCC'den ONCE -- "pakete NE GIRIYOR?"
#   -InstallerPath  : ISCC'den SONRA -- "paketin KENDISI ne oldu?"
#
# Ikincisi EKSIKTI ve bosluk oldugu gibi duruyordu: betik yalnizca publish klasorunu tariyordu ve
# build-installer.ps1/package-installer.ps1 icinde ISCC'den ONCE calisiyordu, yani uretilen
# artifacts\installer\AdaPanelSetup.exe HIC DENETLENMIYORDU. Bu makinedeki PE bulastiricisi tam da
# diskte duran bitmis exe'leri (her acilistan ~6 dk sonra) yeniden bulastiriyordu; yani en kritik
# artefakt, denetimin gormedigi tek artefakti.

param(
    # Mandatory DEGIL: -DefineFunctionsOnly ile dot-source edilirken PowerShell zorunlu parametre
    # icin ISTEM ACAR ve test kosumu orada kilitlenir. Zorunluluk asagida ELLE denetlenir.
    [string] $PublishDir,
    # ISCC'nin urettigi kurulum paketi. -PublishDir ile birlikte de verilebilir, tek basina da.
    [string] $InstallerPath,
    # Paketin tasimasi gereken surum. Yanlis damgalanmis paket, kurulu panellerde SONSUZ GUNCELLEME
    # DONGUSUNE yol acan bilinen bir ariza (bkz. scripts\version-guard.ps1 aciklamasi).
    [string] $ExpectedVersion,
    [string] $ManifestPath,
    # Yalnizca testler icin: fonksiyonlari tanimla, hicbir sey calistirma.
    [switch] $DefineFunctionsOnly
)

$ErrorActionPreference = "Stop"

# BEKLENEN CALISTIRILABILIR DOSYALAR (publish kokune gore, kucuk/buyuk harf duyarsiz).
# Bu liste OLCULEREK dolduruldu: gercek bir self-contained publish ciktisi tarandi.
# Bagimlilik degisince bu liste de degisir -- bilerek: yeni bir ikili sessizce paketlenemesin,
# birinin BAKIP onaylamasi gerekssin.
$script:BeklenenIkililer = @(
    "AdaPanel.Api.exe",
    # .htaccess IIS modulu (net48, guclu adli, AnyCPU). Publish ciktisindan GELMEZ; onu
    # build-installer.ps1 ayrica derleyip buraya kopyalar (net48 hedefi net8 publish'e giremez).
    # Hedef sunucuda GAC'a kurulur ve applicationHost.config'e kaydedilir. Bu satir, "yeni bir
    # ikili sessizce paketlenemesin" kuralinin BILINCLI onayidir.
    "AdaPanel.HtaccessModule.dll",
    "aspnetcorev2_inprocess.dll",
    "D3DCompiler_47_cor3.dll",
    "e_sqlite3.dll",
    "Microsoft.Data.SqlClient.SNI.dll",
    "PenImc_cor3.dll",
    "PresentationNative_cor3.dll",
    "System.Security.Permissions.dll",
    "vcruntime140_cor3.dll",
    "wpfgfx_cor3.dll"
)

# IMZASIZ olmasina izin verilenler (OLCULDU: publish ciktisinda bu ikisi imzasiz gelir).
# Digerlerinin imzasi gecerli DEGILSE dururuz; imzali bir ikilinin imzasini kaybetmesi
# degistirilmis olmasinin en sik isaretidir.
$script:ImzasizIzinliler = @(
    "AdaPanel.Api.exe",
    # Kendi urettigimiz derleme; paket henuz KOD IMZASIZ (bkz. docs/calisma-durumu.md, ticari karar).
    # GUCLU AD imzasi AYRI bir seydir ve VARDIR -- GAC kurulumunun on kosulu odur, Authenticode degil.
    "AdaPanel.HtaccessModule.dll",
    "e_sqlite3.dll"
)

# Beklenen yayincilar (imza konusunun CN'i ile TAM eslesme).
$script:IzinliYayincilar = @(
    "Microsoft Corporation",
    ".NET",
    "Microsoft Windows Software Compatibility Publisher"
)

# BULASMA IMZALARI. "Synaptics" bu makinede gorulen EXE bulastiricisinin biraktigi CompanyName'dir:
# yuk, mesru bir dokunmatik yuzey suruculugu kilifina buruyor.
$script:YasakSirketler = @(
    "Synaptics"
)

# URETILEN KURULUM PAKETININ TASIMASI GEREKEN KIMLIK.
# OLCULDU (artifacts\installer\AdaPanelSetup.exe, 0.5.0): CompanyName="AdaPanel",
# ProductName="AdaPanel", FileDescription="AdaPanel Setup", Authenticode=NotSigned.
# Degerler installer\AdaPanel.iss icindeki MyAppPublisher/MyAppName tanimlarindan gelir; ikisinin
# ayrisamamasi InstallerArtifactVerificationTests icinde ayrica sinanir.
$script:BeklenenPaketSirketi = "AdaPanel"
$script:BeklenenPaketUrunu = "AdaPanel"

function Test-AdaPortableExecutable {
    <#
        Dosya bir Windows calistirilabiliri mi? UZANTIYA BAKMAZ -- MZ basligini ve PE imzasini
        okur. Bulastirici yukunu ".dat"/".jpg" adiyla birakirsa uzanti suzgeci onu kacirirdi.
    #>
    param([Parameter(Mandatory = $true)][string] $Yol)

    try {
        $akis = [System.IO.File]::OpenRead($Yol)
    } catch {
        return $false
    }

    try {
        if ($akis.Length -lt 64) { return $false }

        $imzaBaytlari = New-Object byte[] 2
        if ($akis.Read($imzaBaytlari, 0, 2) -ne 2) { return $false }
        if ($imzaBaytlari[0] -ne 0x4D -or $imzaBaytlari[1] -ne 0x5A) { return $false }   # "MZ"

        $akis.Position = 0x3C
        $ofsetBaytlari = New-Object byte[] 4
        if ($akis.Read($ofsetBaytlari, 0, 4) -ne 4) { return $false }
        $peOfseti = [BitConverter]::ToInt32($ofsetBaytlari, 0)
        if ($peOfseti -le 0 -or ($peOfseti + 4) -gt $akis.Length) { return $false }

        $akis.Position = $peOfseti
        $pe = New-Object byte[] 4
        if ($akis.Read($pe, 0, 4) -ne 4) { return $false }

        # "PE\0\0"
        return ($pe[0] -eq 0x50 -and $pe[1] -eq 0x45 -and $pe[2] -eq 0 -and $pe[3] -eq 0)
    }
    finally {
        $akis.Dispose()
    }
}

function Get-AdaFileMetadata {
    <#
        Bir PE dosyasinin imza ve yayinci bilgisi. Testler -MetadataProvider ile kendi
        saglayicisini gecirebilir: "bulasik dosya" durumu bu makinede gercekten uretilemez,
        ama karar mantiginin dogrulanmasi gerekir.
    #>
    param([Parameter(Mandatory = $true)][string] $Yol)

    $imza = Get-AuthenticodeSignature -LiteralPath $Yol
    $yayinci = ""
    if ($imza.SignerCertificate) {
        $eslesme = [regex]::Match($imza.SignerCertificate.Subject, '(?:^|,\s*)CN=("([^"]*)"|[^,]*)')
        if ($eslesme.Success) {
            $yayinci = $(if ($eslesme.Groups[2].Success) { $eslesme.Groups[2].Value } else { $eslesme.Groups[1].Value }).Trim()
        }
    }

    $surumBilgisi = (Get-Item -LiteralPath $Yol).VersionInfo
    return [pscustomobject]@{
        ImzaDurumu = "$($imza.Status)"
        Yayinci    = $yayinci
        Sirket     = "$($surumBilgisi.CompanyName)".Trim()
        Urun       = "$($surumBilgisi.ProductName)".Trim()
        Surum      = "$($surumBilgisi.ProductVersion)".Trim()
    }
}

function Get-AdaPublishFinding {
    <#
        Tek bir dosya icin KARAR. Bulgu dizisi doner; bos dizi = temiz.
        Karar mantigi burada toplanmistir ki test edilebilsin.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $GoreceliYol,
        [Parameter(Mandatory = $true)][bool] $Calistirilabilir,
        [object] $UstVeri
    )

    $bulgular = [System.Collections.Generic.List[string]]::new()
    if (-not $Calistirilabilir) { return $bulgular.ToArray() }

    $ad = Split-Path $GoreceliYol -Leaf
    if ($script:BeklenenIkililer -notcontains $ad) {
        $bulgular.Add("BEKLENMEYEN CALISTIRILABILIR: $GoreceliYol -- bu dosya publish ciktisinda olmamali. " +
                      "Mesru bir yeni bagimliliksa scripts\verify-publish-output.ps1 icindeki listeye BAKILARAK eklenmeli.")
        # Beklenmeyen dosya zaten yeterli sebep; yine de yayinci bilgisini rapora tasi.
    }

    if ($null -eq $UstVeri) { return $bulgular.ToArray() }

    foreach ($yasak in $script:YasakSirketler) {
        if ($UstVeri.Sirket -and $UstVeri.Sirket -like "*$yasak*") {
            $bulgular.Add("BULASMA IMZASI: $GoreceliYol CompanyName='$($UstVeri.Sirket)'. Paket URETILMEDI.")
        }
    }

    if ($UstVeri.ImzaDurumu -eq "Valid") {
        if ($script:IzinliYayincilar -notcontains $UstVeri.Yayinci) {
            $bulgular.Add("BEKLENMEDIK YAYINCI: $GoreceliYol imzali ama CN='$($UstVeri.Yayinci)'.")
        }
    }
    elseif ($script:ImzasizIzinliler -notcontains $ad) {
        $bulgular.Add("IMZA GECERSIZ: $GoreceliYol durum='$($UstVeri.ImzaDurumu)'. " +
                      "Bu dosya imzali gelmeli; imzasini kaybetmesi degistirildiginin isaretidir.")
    }

    return $bulgular.ToArray()
}

function Invoke-AdaPublishVerification {
    param(
        [Parameter(Mandatory = $true)][string] $PublishDir,
        [string] $ManifestPath,
        # Testler icin: ust veri saglayicisini degistir. Varsayilan GERCEK okuyucudur.
        [scriptblock] $MetadataProvider
    )

    if (-not (Test-Path -LiteralPath $PublishDir)) {
        throw "Publish klasoru yok: $PublishDir"
    }

    $kok = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\')
    $girdiler = [System.Collections.Generic.List[object]]::new()
    $bulgular = [System.Collections.Generic.List[string]]::new()

    foreach ($dosya in (Get-ChildItem -LiteralPath $kok -Recurse -File -Force | Sort-Object FullName)) {
        $gorece = $dosya.FullName.Substring($kok.Length).TrimStart('\')
        $calistirilabilir = Test-AdaPortableExecutable -Yol $dosya.FullName

        $ustVeri = $null
        if ($calistirilabilir) {
            $ustVeri = if ($MetadataProvider) { & $MetadataProvider $dosya.FullName } else { Get-AdaFileMetadata -Yol $dosya.FullName }
        }

        foreach ($bulgu in (Get-AdaPublishFinding -GoreceliYol $gorece -Calistirilabilir $calistirilabilir -UstVeri $ustVeri)) {
            $bulgular.Add($bulgu)
        }

        $girdiler.Add([pscustomobject]@{
            path             = ($gorece -replace '\\', '/')
            bytes            = $dosya.Length
            sha256           = (Get-FileHash -LiteralPath $dosya.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            executable       = $calistirilabilir
            signatureStatus  = $(if ($ustVeri) { $ustVeri.ImzaDurumu } else { $null })
            publisher        = $(if ($ustVeri) { $ustVeri.Yayinci } else { $null })
            company          = $(if ($ustVeri) { $ustVeri.Sirket } else { $null })
        })
    }

    # Manifest, DENETIM DUSSE BILE yazilir: neyin reddedildigi incelenebilmeli.
    if ($ManifestPath) {
        New-Item -ItemType Directory -Path (Split-Path $ManifestPath -Parent) -Force | Out-Null
        [pscustomobject]@{
            publishDir  = $kok
            generatedAt = (Get-Date).ToUniversalTime().ToString("o")
            fileCount   = $girdiler.Count
            findings    = $bulgular.ToArray()
            files       = $girdiler.ToArray()
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ManifestPath -Encoding utf8
    }

    if ($bulgular.Count -gt 0) {
        throw "Publish icerik denetimi BASARISIZ -- paket uretilmedi:`n  " + ($bulgular -join "`n  ")
    }

    return [pscustomobject]@{
        PublishDir   = $kok
        FileCount    = $girdiler.Count
        ManifestPath = $ManifestPath
        Files        = $girdiler.ToArray()
    }
}

function Get-AdaInstallerFinding {
    <#
        URETILEN KURULUM PAKETI icin KARAR. Bulgu dizisi doner; bos dizi = temiz.
        Saf fonksiyon: dosya sistemine dokunmaz, boylece "bulasik paket" durumu -- bu makinede
        URETILEMEYECEK bir durum -- yine de sinanabilir.
    #>
    param(
        [Parameter(Mandatory = $true)][bool] $Calistirilabilir,
        [object] $UstVeri,
        [long] $Boyut = 0,
        # ISCC'ye verilen surum. Verilirse paketin damgasiyla eslesmek ZORUNDA: yanlis damgalanmis
        # paket, kurulu panellerde sonsuz guncelleme dongusune yol acan bilinen bir arizadir.
        [string] $BeklenenSurum
    )

    $bulgular = [System.Collections.Generic.List[string]]::new()

    if (-not $Calistirilabilir) {
        $bulgular.Add("KURULUM PAKETI CALISTIRILABILIR DEGIL: dosya bir PE degil (yarim yazilmis ya da ISCC uretmemis).")
        return $bulgular.ToArray()
    }

    # Bos/kucuk dosya = ISCC yarida kalmis. Paketin gercek boyutu ~50-90 MiB; 1 MiB esigi bunu
    # yakalayacak kadar dusuk, mesru bir kucuk paketi reddetmeyecek kadar gevsek.
    if ($Boyut -gt 0 -and $Boyut -lt 1MB) {
        $bulgular.Add("KURULUM PAKETI COK KUCUK: $Boyut bayt. ISCC ciktisi yarim kalmis olabilir.")
    }

    if ($null -eq $UstVeri) { return $bulgular.ToArray() }

    foreach ($yasak in $script:YasakSirketler) {
        if (($UstVeri.Sirket -and $UstVeri.Sirket -like "*$yasak*") -or
            ($UstVeri.Urun -and $UstVeri.Urun -like "*$yasak*")) {
            $bulgular.Add("BULASMA IMZASI: uretilen kurulum paketi CompanyName='$($UstVeri.Sirket)' " +
                          "ProductName='$($UstVeri.Urun)'. PAKET YAYINLANMAMALI.")
        }
    }

    if ($UstVeri.Sirket -ne $script:BeklenenPaketSirketi) {
        $bulgular.Add("BEKLENMEDIK YAYINCI: kurulum paketinde CompanyName='$($UstVeri.Sirket)', " +
                      "beklenen '$script:BeklenenPaketSirketi' (installer\AdaPanel.iss -> MyAppPublisher).")
    }

    if ($UstVeri.Urun -ne $script:BeklenenPaketUrunu) {
        $bulgular.Add("BEKLENMEDIK URUN: kurulum paketinde ProductName='$($UstVeri.Urun)', " +
                      "beklenen '$script:BeklenenPaketUrunu' (installer\AdaPanel.iss -> MyAppName).")
    }

    if ($BeklenenSurum -and $UstVeri.Surum) {
        $paketSurumu = $UstVeri.Surum.Split('+')[0].Trim()
        $esit = $paketSurumu -eq $BeklenenSurum
        if (-not $esit) {
            # "0.6.4" ile "0.6.4.0" AYNI surumdur: kaynak uc parcali yazsa da Inno alan sayisini
            # tamamlayabilir. Duz dize karsilastirmasi burada yanlis alarm uretirdi.
            $paketNesnesi = $null
            $beklenenNesnesi = $null
            if ([version]::TryParse($paketSurumu, [ref]$paketNesnesi) -and
                [version]::TryParse($BeklenenSurum, [ref]$beklenenNesnesi)) {
                $esit = ($paketNesnesi.Major -eq $beklenenNesnesi.Major) -and
                        ($paketNesnesi.Minor -eq $beklenenNesnesi.Minor) -and
                        ([Math]::Max($paketNesnesi.Build, 0) -eq [Math]::Max($beklenenNesnesi.Build, 0)) -and
                        ([Math]::Max($paketNesnesi.Revision, 0) -eq [Math]::Max($beklenenNesnesi.Revision, 0))
            }
        }

        if (-not $esit) {
            $bulgular.Add("SURUM DAMGASI TUTMUYOR: pakette '$($UstVeri.Surum)', beklenen '$BeklenenSurum'. " +
                          "Yanlis damgalanmis paket kurulu panellerde sonsuz guncelleme dongusu yapar.")
        }
    }

    # IMZA: paket bugun IMZASIZ uretiliyor (olculdu: Authenticode=NotSigned, depoda signtool adimi
    # yok). Bu yuzden "imzali olmali" DENMEZ -- yalan bir guvence olurdu. Ama imza VARSA gecerli
    # olmak zorundadir: imzali bir ikilinin imzasini kaybetmesi, degistirilmis olmasinin en guclu
    # isaretidir. Sertifika alindiginda burasi "Valid degilse DUR"a cevrilir.
    if ($UstVeri.ImzaDurumu -eq "Valid") {
        if ($script:IzinliYayincilar -notcontains $UstVeri.Yayinci -and $UstVeri.Yayinci -ne $script:BeklenenPaketSirketi) {
            $bulgular.Add("BEKLENMEDIK IMZA SAHIBI: kurulum paketi imzali ama CN='$($UstVeri.Yayinci)'.")
        }
    }
    elseif ($UstVeri.ImzaDurumu -ne "NotSigned") {
        $bulgular.Add("IMZA GECERSIZ: kurulum paketi imzali gorunuyor ama durum='$($UstVeri.ImzaDurumu)'.")
    }

    return $bulgular.ToArray()
}

function Invoke-AdaInstallerVerification {
    <#
        ISCC'den SONRA calisir: uretilen AdaPanelSetup.exe'nin KENDISINI denetler ve kaydini
        (boyut + SHA256 + kimlik + imza) manifest'e yazar.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $InstallerPath,
        [string] $ManifestPath,
        [string] $BeklenenSurum,
        [scriptblock] $MetadataProvider
    )

    if (-not (Test-Path -LiteralPath $InstallerPath)) {
        throw "Kurulum paketi yok: $InstallerPath"
    }

    $dosya = Get-Item -LiteralPath $InstallerPath
    $calistirilabilir = Test-AdaPortableExecutable -Yol $dosya.FullName
    $ustVeri = $null
    if ($calistirilabilir) {
        $ustVeri = if ($MetadataProvider) { & $MetadataProvider $dosya.FullName } else { Get-AdaFileMetadata -Yol $dosya.FullName }
    }

    $bulgular = @(Get-AdaInstallerFinding `
        -Calistirilabilir $calistirilabilir `
        -UstVeri $ustVeri `
        -Boyut $dosya.Length `
        -BeklenenSurum $BeklenenSurum)

    # Manifest DENETIM DUSSE BILE yazilir: neyin reddedildigi incelenebilmeli.
    if ($ManifestPath) {
        New-Item -ItemType Directory -Path (Split-Path $ManifestPath -Parent) -Force | Out-Null
        [pscustomobject]@{
            installerPath   = $dosya.FullName
            generatedAt     = (Get-Date).ToUniversalTime().ToString("o")
            bytes           = $dosya.Length
            sha256          = (Get-FileHash -LiteralPath $dosya.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            executable      = $calistirilabilir
            signatureStatus = $(if ($ustVeri) { $ustVeri.ImzaDurumu } else { $null })
            publisher       = $(if ($ustVeri) { $ustVeri.Yayinci } else { $null })
            company         = $(if ($ustVeri) { $ustVeri.Sirket } else { $null })
            product         = $(if ($ustVeri) { $ustVeri.Urun } else { $null })
            productVersion  = $(if ($ustVeri) { $ustVeri.Surum } else { $null })
            findings        = $bulgular
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ManifestPath -Encoding utf8
    }

    if ($bulgular.Count -gt 0) {
        throw "Kurulum paketi denetimi BASARISIZ -- paket YAYINLANMAMALI:`n  " + ($bulgular -join "`n  ")
    }

    return [pscustomobject]@{
        InstallerPath = $dosya.FullName
        Bytes         = $dosya.Length
        ManifestPath  = $ManifestPath
    }
}

if ($DefineFunctionsOnly) { return }

if ([string]::IsNullOrWhiteSpace($PublishDir) -and [string]::IsNullOrWhiteSpace($InstallerPath)) {
    throw "-PublishDir ya da -InstallerPath zorunludur."
}

if (-not [string]::IsNullOrWhiteSpace($PublishDir)) {
    $sonuc = Invoke-AdaPublishVerification -PublishDir $PublishDir -ManifestPath $ManifestPath
    Write-Host ("Publish icerik denetimi GECTI: {0} dosya denetlendi." -f $sonuc.FileCount)
    if ($sonuc.ManifestPath) {
        Write-Host ("Manifest: {0}" -f $sonuc.ManifestPath)
    }
}

if (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
    $paket = Invoke-AdaInstallerVerification -InstallerPath $InstallerPath -ManifestPath $ManifestPath -BeklenenSurum $ExpectedVersion
    Write-Host ("Kurulum paketi denetimi GECTI: {0} ({1:N0} bayt)." -f $paket.InstallerPath, $paket.Bytes)
    if ($paket.ManifestPath) {
        Write-Host ("Manifest: {0}" -f $paket.ManifestPath)
    }
}
