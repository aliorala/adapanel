# GitHub release yayinlama -- TEK KAYNAK.
#
# NEDEN VAR: 0.6.1 release notlarinda mojibake cikti ("Artik" -> "ArtÄ±k"). Kok neden: her
# yayinda bu API cagrisi SOHBETE elle yaziliyordu ve o seferinde govde
#   Get-Content release-body.json -Raw
# ile okunmustu (-Encoding belirtilmeden). release-body.json BOM'suz UTF-8; Windows PowerShell
# 5.1'de Get-Content -Raw boyle bir dosyada sistem kod sayfasini varsayar, Turkce karakterlerin
# coklu-bayt UTF-8 dizilerini tek-baytlik harfler sanip bozar. Sonraki adim bu ZATEN BOZUK
# dizgeyi tekrar UTF-8'e cevirip GitHub'a yolladi -- cift bozulma.
#
# Cozum bir seferlik PATCH ile govdeyi duzeltmek degildi (o YAMA olurdu, sonraki yayinda ayni
# hata tekrar eder). Cozum: dosyayi ACIKCA UTF-8 okuyan TEK bir betik yazip butun yayinlari
# (ilk yayin da, duzeltme de) hep bu betikten gecirmek.
#
# KULLANIM:
#   powershell -File scripts\publish-github-release.ps1 `
#       -InstallerPath artifacts\installer\AdaPanelSetup.exe
#
# Yalnizca release notlarini olusturmak/duzeltmek icin (varlik yuklemeden):
#   powershell -File scripts\publish-github-release.ps1
param(
    [string]$RepoRoot,
    [string]$RepoSlug = "aliorala/adapanel",
    [string]$InstallerPath,
    [switch]$SkipAssetUpload,
    [switch]$SkipVerify
)
$ErrorActionPreference = "Stop"

if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }

function Get-AdaGitHubToken {
    <#
        OLCULDU: git credential fill'e CRLF'li istek gonderilirse (Set-Content'in varsayilani)
        credential helper istegi parse edemiyor. Istek dosyasi LF-only, BOM'suz yazilmali.
    #>
    $reqPath = [System.IO.Path]::GetTempFileName()
    try {
        $istek = "protocol=https`nhost=github.com`n`n"
        [System.IO.File]::WriteAllText($reqPath, $istek, [System.Text.UTF8Encoding]::new($false))
        $out = cmd /c "git credential fill < `"$reqPath`""
        $tok = ($out | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
        if ([string]::IsNullOrWhiteSpace($tok)) {
            throw "git credential fill'den token alinamadi. GitHub'a bir kez 'git credential approve' ile giris yapilmis olmali."
        }
        return $tok.Trim()
    } finally {
        Remove-Item -LiteralPath $reqPath -Force -ErrorAction SilentlyContinue
    }
}

function Read-AdaReleaseBody {
    <#
        BUNUN DISINDA HICBIR YERDE release-body.json OKUNMAMALI. Get-Content -Raw DEGIL --
        [System.IO.File]::ReadAllText + acik UTF8 encoding. Yukaridaki dosya basi aciklamasi
        bu fonksiyonun neden var oldugunu anlatir.
    #>
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw "release-body.json bulunamadi: $Path" }

    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    $obj = $text | ConvertFrom-Json

    foreach ($alan in @('tag_name', 'name', 'body')) {
        $deger = $obj.PSObject.Properties[$alan]
        if ($null -eq $deger -or [string]::IsNullOrWhiteSpace("$($deger.Value)")) {
            throw "release-body.json icinde '$alan' eksik: $Path"
        }
    }
    return $obj
}

function Invoke-AdaGitHubApi {
    <#
        Butun GitHub cagrilari BURADAN gecer ki govde kodlamasi tek yerde dogru yapilsin.
        PowerShell dizgesi dogrudan Invoke-RestMethod'a verilmez (o zaman .NET'in varsayilan
        kodlamasina birakilmis olur) -- her zaman ACIKCA UTF-8 bayt dizisine cevrilir.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Uri,
        [string] $Method = "GET",
        [Parameter(Mandatory = $true)][string] $Token,
        [object] $BodyObject
    )

    $headers = @{ Authorization = "token $Token"; "User-Agent" = "AdaPanel-Release-Script" }
    $params = @{ Uri = $Uri; Method = $Method; Headers = $headers }

    if ($null -ne $BodyObject) {
        $json = $BodyObject | ConvertTo-Json -Depth 10
        $params["Body"] = [System.Text.Encoding]::UTF8.GetBytes($json)
        $params["ContentType"] = "application/json; charset=utf-8"
    }

    return Invoke-RestMethod @params
}

function Publish-AdaGitHubRelease {
    param(
        [Parameter(Mandatory = $true)][string] $RepoSlug,
        [Parameter(Mandatory = $true)][string] $Token,
        [Parameter(Mandatory = $true)][pscustomobject] $ReleaseInfo
    )

    $existing = $null
    try {
        $existing = Invoke-AdaGitHubApi -Uri "https://api.github.com/repos/$RepoSlug/releases/tags/$($ReleaseInfo.tag_name)" -Token $Token
    } catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -ne 404) { throw }
    }

    $payload = @{ tag_name = $ReleaseInfo.tag_name; name = $ReleaseInfo.name; body = $ReleaseInfo.body }

    if ($existing) {
        Write-Host "Release zaten var (id=$($existing.id), tag=$($ReleaseInfo.tag_name)) -- notlar PATCH ile guncelleniyor."
        $result = Invoke-AdaGitHubApi -Uri "https://api.github.com/repos/$RepoSlug/releases/$($existing.id)" -Method PATCH -Token $Token -BodyObject $payload
    } else {
        Write-Host "Yeni release olusturuluyor: $($ReleaseInfo.tag_name)"
        $result = Invoke-AdaGitHubApi -Uri "https://api.github.com/repos/$RepoSlug/releases" -Method POST -Token $Token -BodyObject $payload
    }

    # KAYIT DEGIL DOGRULAMA: gonderilen govde API'den GERI okunup kaynakla BIREBIR karsilastirilir.
    # Mojibake ya da baska bir kodlama kaybi varsa burada YAKALANIR, sessizce yayinlanmis olmaz.
    $roundtrip = Invoke-AdaGitHubApi -Uri "https://api.github.com/repos/$RepoSlug/releases/$($result.id)" -Token $Token
    if ($roundtrip.body -ne $ReleaseInfo.body) {
        throw "Yayinlanan release govdesi kaynak dosyayla BIREBIR eslesmiyor (kodlama sorunu olabilir). Release id=$($result.id) -- elle kontrol edin."
    }
    Write-Host "Govde dogrulandi: yayinlanan metin kaynakla birebir ayni."

    return $roundtrip
}

function Add-AdaReleaseAsset {
    param(
        [Parameter(Mandatory = $true)][string] $RepoSlug,
        [Parameter(Mandatory = $true)][pscustomobject] $Release,
        [Parameter(Mandatory = $true)][string] $Token,
        [Parameter(Mandatory = $true)][string] $FilePath
    )

    if (-not (Test-Path -LiteralPath $FilePath)) { throw "Yuklenecek dosya bulunamadi: $FilePath" }

    $name = [System.IO.Path]::GetFileName($FilePath)
    $mevcut = $Release.assets | Where-Object { $_.name -eq $name }
    if ($mevcut) {
        Write-Host "Ayni adda varlik zaten var (id=$($mevcut.id)) -- once siliniyor."
        Invoke-AdaGitHubApi -Uri "https://api.github.com/repos/$RepoSlug/releases/assets/$($mevcut.id)" -Method DELETE -Token $Token | Out-Null
    }

    $uploadUri = ($Release.upload_url -replace '\{\?name,label\}', '') + "?name=$name"
    $headers = @{ Authorization = "token $Token"; "User-Agent" = "AdaPanel-Release-Script" }
    Write-Host "Yukleniyor: $name"
    $asset = Invoke-RestMethod -Uri $uploadUri -Method Post -Headers $headers -InFile $FilePath -ContentType 'application/octet-stream'
    Write-Host "Yuklendi: $($asset.name) ($('{0:N0}' -f $asset.size) byte, durum=$($asset.state))"
    return $asset
}

function Test-AdaPublishedRelease {
    <#Yayinlanan varligi INDIRIP yereldeki dosyayla SHA256 karsilastirir; kayit degil dogrulamadir.#>
    param(
        [Parameter(Mandatory = $true)][string] $RepoSlug,
        [Parameter(Mandatory = $true)][string] $ExpectedTag,
        [Parameter(Mandatory = $true)][string] $LocalInstallerPath
    )

    $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$RepoSlug/releases/latest" -Headers @{ "User-Agent" = "AdaPanel-Release-Script" }
    if ($latest.tag_name -ne $ExpectedTag) {
        throw "releases/latest = $($latest.tag_name), beklenen = $ExpectedTag."
    }

    $asset = $latest.assets | Select-Object -First 1
    if (-not $asset) { throw "Yayinlanmis release'de varlik (asset) yok." }

    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmp -UseBasicParsing
        $indirilen = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash
        $yerel = (Get-FileHash -LiteralPath $LocalInstallerPath -Algorithm SHA256).Hash
        if ($indirilen -ne $yerel) {
            throw "Hash uyusmuyor -- indirilen: $indirilen, yerel: $yerel."
        }
        Write-Host "Dogrulandi: indirilen varlik yerel dosyayla birebir ayni (SHA256 $indirilen)."
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

# --- Betik govdesi ---------------------------------------------------------
if ($MyInvocation.InvocationName -ne '.') {
    . (Join-Path $PSScriptRoot "version-guard.ps1")
    $version = Resolve-AdaReleaseVersion -RepoRoot $RepoRoot
    Write-Host "Surum: $version"

    $releaseInfo = Read-AdaReleaseBody -Path (Join-Path $RepoRoot "release-body.json")
    if ($releaseInfo.tag_name -ne $version) {
        throw "release-body.json tag_name ($($releaseInfo.tag_name)) surumle ($version) uyusmuyor."
    }

    $token = Get-AdaGitHubToken
    $release = Publish-AdaGitHubRelease -RepoSlug $RepoSlug -Token $token -ReleaseInfo $releaseInfo

    if ($InstallerPath -and -not $SkipAssetUpload) {
        Add-AdaReleaseAsset -RepoSlug $RepoSlug -Release $release -Token $token -FilePath $InstallerPath | Out-Null
        if (-not $SkipVerify) {
            Test-AdaPublishedRelease -RepoSlug $RepoSlug -ExpectedTag $releaseInfo.tag_name -LocalInstallerPath $InstallerPath
        }
    }

    Write-Host "Tamamlandi: https://github.com/$RepoSlug/releases/tag/$($releaseInfo.tag_name)"
}
