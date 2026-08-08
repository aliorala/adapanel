# SURUM KAPISI -- paketleme hatlarinin TEK surum kaynagi.
#
# NEDEN VAR: build-installer.ps1 ve package-installer.ps1 ikisi de "$Version = '0.2.28'" diye OLU
# bir varsayilan tasiyordu ve HICBIRI Directory.Build.props'u okumuyordu. README'deki komut
# parametresizdi. Zincir sudur:
#
#   README'deki komutu calistir -> kaynak 0.4.22 iken exe 0.2.28 damgalanir
#   -> release "0.4.23" tag'iyle yayinlanir
#   -> kurulu paneller 'latest'i gorur, SHA dogru oldugu icin gecer, kurar
#   -> panel 0.2.28'e DUSER -> bir sonraki kontrolde yine 0.4.23 gorur
#   -> her gun indirip kuran SONSUZ GUNCELLEME DONGUSU.
#
# Cozum bir varsayilani duzeltmek degil: varsayilani KALDIRMAK ve surumu kaynaktan OKUMAK.
# Surum su an bes ayri yerde yaziyor; hicbiri digerini dogrulamiyordu. Bu betik dogrular.
#
# KULLANIM (dot-source):
#   . "$PSScriptRoot\version-guard.ps1"
#   $Version = Resolve-AdaReleaseVersion -RepoRoot $repoRoot -RequestedVersion $Version

# NOT: burada bilerek Set-StrictMode YOK. Bu betik "eksik/bozuk alan" durumunu ANLAMLI bir hata
# mesajina cevirmek icin var; strict mode ayni durumu once kendi istisnasina cevirip mesaji yutar.
function Get-AdaJsonAlan {
    <#Bir JSON dosyasindan alan okur; dosya/alan yoksa $null doner (istisna atmaz).#>
    param([string] $Yol, [string] $Alan)

    if (-not (Test-Path -LiteralPath $Yol)) { return $null }

    try {
        $nesne = Get-Content -LiteralPath $Yol -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    } catch {
        return $null
    }

    if ($null -eq $nesne) { return $null }
    $ozellik = $nesne.PSObject.Properties[$Alan]
    if ($null -eq $ozellik -or $null -eq $ozellik.Value) { return $null }

    return "$($ozellik.Value)".Trim()
}

function Get-AdaPackageLockVersion {
    <#
        package-lock.json'i ConvertFrom-Json ILE OKUYAMAYIZ -- OLCULDU:
        Windows PowerShell 5.1 su hatayi verir:
            "Cannot process argument because the value of argument 'name' is not valid."
        Sebep: kilit dosyasinda "packages" altinda BOS DIZGE ("") anahtari var; 5.1'in JSON
        cozumleyicisi PSObject ozelligi olarak bos ad kabul etmiyor. Yani "ConvertFrom-Json
        basarisiz -> surum okunamadi" demek dosyayi yanlis suclamak olurdu.
        Bu yuzden yalnizca UST DUZEY "version" alani metinden okunur.
    #>
    param([string] $Yol)

    if (-not (Test-Path -LiteralPath $Yol)) { return $null }

    $metin = Get-Content -LiteralPath $Yol -Raw
    $eslesme = [regex]::Match($metin, '^\s*\{[^{]*?"version"\s*:\s*"([^"]+)"', 'Singleline')
    if (-not $eslesme.Success) { return $null }

    return $eslesme.Groups[1].Value.Trim()
}

function Get-AdaSourceVersion {
    <#
        Directory.Build.props = surumun TEK KAYNAGI. .NET derlemesi AssemblyVersion/FileVersion/
        InformationalVersion'i bundan turetir; panelin kendi bildirdigi surum budur.
    #>
    param([Parameter(Mandatory = $true)][string] $RepoRoot)

    $propsPath = Join-Path $RepoRoot "Directory.Build.props"
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "Surum kaynagi bulunamadi: $propsPath"
    }

    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $dugum = $props.SelectSingleNode("/Project/PropertyGroup/Version")
    $version = if ($dugum) { "$($dugum.InnerText)".Trim() } else { $null }

    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Directory.Build.props icinde <Version> yok: $propsPath"
    }

    return $version
}

function Get-AdaVersionSources {
    <#
        Surumun yazili oldugu HER yeri okur. Okunamayan/eksik dosya sessizce atlanmaz: kaydi
        "Version = $null" ile doner ki cagiran fark etsin.

        Sert (Hard) = uretilen artefakti ya da yayin tag'ini BELIRLER -> uyusmazsa DUR.
        Yumusak (Soft) = uretilen artefakti etkilemez -> uyar, durdurma.
    #>
    param([Parameter(Mandatory = $true)][string] $RepoRoot)

    $kayitlar = [System.Collections.Generic.List[object]]::new()

    function Add-Kayit {
        param([string]$Ad, [string]$Yol, [string]$Surum, [bool]$Sert, [string]$Neden)
        $kayitlar.Add([pscustomobject]@{
            Ad     = $Ad
            Yol    = $Yol
            Surum  = $Surum
            Sert   = $Sert
            Neden  = $Neden
        })
    }

    # frontend/package.json -- npm'in bildirdigi surum, arayuzde gorunur.
    $packageJson = Join-Path $RepoRoot "frontend\package.json"
    Add-Kayit "frontend/package.json" $packageJson (Get-AdaJsonAlan $packageJson "version") $true "Arayuzun bildirdigi surum."

    # installer/AdaPanel.iss -- scriptler /DMyAppVersion ile ezer, ama ELLE derlenirse bu deger
    # damgalanir. Stale kalirsa "elle derlenen paket" yanlis surum tasir.
    $issYol = Join-Path $RepoRoot "installer\AdaPanel.iss"
    $issSurum = $null
    if (Test-Path -LiteralPath $issYol) {
        $eslesme = [regex]::Match((Get-Content -LiteralPath $issYol -Raw), '#define\s+MyAppVersion\s+"([^"]+)"')
        if ($eslesme.Success) { $issSurum = $eslesme.Groups[1].Value.Trim() }
    }
    Add-Kayit "installer/AdaPanel.iss" $issYol $issSurum $true "Elle derlemede damgalanan surum."

    # release-body.json -- GitHub release tag'i. Panelin self-update'i tag ile karsilastirir;
    # tag ile exe farkliysa sonsuz guncelleme dongusu olusur. En kritik esitlik budur.
    $releaseYol = Join-Path $RepoRoot "release-body.json"
    $tag = Get-AdaJsonAlan $releaseYol "tag_name"
    $releaseSurum = if ($tag) { $tag -replace '^[vV]', '' } else { $null }
    Add-Kayit "release-body.json (tag_name)" $releaseYol $releaseSurum $true "GitHub release tag'i; self-update bunu karsilastirir."

    # frontend/package-lock.json -- YUMUSAK. OLCULDU: bu alan hicbir derleme adimi tarafindan
    # okunmuyor, uretilen exe'ye ya da release tag'ine hic girmiyor ve `npm install` onu
    # package.json'dan kendisi yeniden yaziyor. Sert kapiya alinsaydi, artefakti hic etkilemeyen
    # bir alan yuzunden butun yayin hatti kilitlenirdi.
    $lockYol = Join-Path $RepoRoot "frontend\package-lock.json"
    Add-Kayit "frontend/package-lock.json" $lockYol (Get-AdaPackageLockVersion $lockYol) $false "npm install ile yeniden yazilir; artefakti etkilemez."

    return $kayitlar.ToArray()
}

function Resolve-AdaReleaseVersion {
    <#
        Paketlenecek surumu dondurur. Cagiran -RequestedVersion vermezse kaynaktan okunur;
        verirse kaynakla AYNI olmak zorundadir. Sert kaynaklardan biri uymuyorsa THROW eder --
        yanlis damgalanmis bir paket uretmektense hic uretmemek dogrudur.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $RepoRoot,
        [string] $RequestedVersion
    )

    $kaynakSurum = Get-AdaSourceVersion -RepoRoot $RepoRoot

    if ([string]::IsNullOrWhiteSpace($RequestedVersion)) {
        $RequestedVersion = $kaynakSurum
    }
    elseif ($RequestedVersion.Trim() -ne $kaynakSurum) {
        throw "Surum uyusmazligi: -Version=$($RequestedVersion.Trim()), Directory.Build.props=$kaynakSurum. " +
              "Once Directory.Build.props'u guncelleyin; surum tek kaynaktan gelir."
    }

    $sertHatalar = [System.Collections.Generic.List[string]]::new()
    foreach ($kayit in (Get-AdaVersionSources -RepoRoot $RepoRoot)) {
        if ([string]::IsNullOrWhiteSpace($kayit.Surum)) {
            $mesaj = "$($kayit.Ad): surum OKUNAMADI ($($kayit.Yol))"
            if ($kayit.Sert) { $sertHatalar.Add($mesaj) } else { Write-Warning $mesaj }
            continue
        }

        if ($kayit.Surum -ne $kaynakSurum) {
            $mesaj = "$($kayit.Ad) = $($kayit.Surum), Directory.Build.props = $kaynakSurum ($($kayit.Neden))"
            if ($kayit.Sert) { $sertHatalar.Add($mesaj) } else { Write-Warning "Surum farki (bloke etmez): $mesaj" }
        }
    }

    if ($sertHatalar.Count -gt 0) {
        throw "Surum tutarsizligi -- paket URETILMEDI:`n  " + ($sertHatalar -join "`n  ") +
              "`nSurum tek kaynaktan (Directory.Build.props) gelir; digerlerini ona esitleyin."
    }

    return $kaynakSurum
}
