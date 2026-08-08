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

param(
    # Mandatory DEGIL: -DefineFunctionsOnly ile dot-source edilirken PowerShell zorunlu parametre
    # icin ISTEM ACAR ve test kosumu orada kilitlenir. Zorunluluk asagida ELLE denetlenir.
    [string] $PublishDir,
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

    return [pscustomobject]@{
        ImzaDurumu = "$($imza.Status)"
        Yayinci    = $yayinci
        Sirket     = "$((Get-Item -LiteralPath $Yol).VersionInfo.CompanyName)".Trim()
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

if ($DefineFunctionsOnly) { return }

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    throw "-PublishDir zorunludur."
}

$sonuc = Invoke-AdaPublishVerification -PublishDir $PublishDir -ManifestPath $ManifestPath
Write-Host ("Publish icerik denetimi GECTI: {0} dosya denetlendi." -f $sonuc.FileCount)
if ($sonuc.ManifestPath) {
    Write-Host ("Manifest: {0}" -f $sonuc.ManifestPath)
}
