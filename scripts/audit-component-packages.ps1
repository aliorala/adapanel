# BILESEN PAKETLERININ DENETIMI -- "manifestteki adres gercekten calisiyor mu, indirilen dosya
# gercekten beklenen dosya mi" sorusunu cevaplar.
#
# IKI SINIF BILESEN VAR (0.5.0 paketlenirken CANLI olarak ogrenildi):
#
#   1) Manifest adresi KURULUMDA KULLANILAN adrestir (vc-redist, IIS eklentileri, docker-engine,
#      composer, phpMyAdmin...). Bu adres kirilirsa kurulum da kirilir -> yayin DURMALIDIR.
#
#   2) Manifest adresi yalnizca YEDEK yoldur: ComponentBootstrapper bu bilesenler icin ust
#      kaynaktan guncel paketi CANLI cozer (php-8.3 -> downloads.php.net/releases.json,
#      mariadb-11 -> archive.mariadb.org, nginx -> nginx.org ...). Ust kaynak yeni yama cikarip
#      eskisini arsive tasidiginda yedek adres 404 verir; KURULUM BOZULMAZ ama yedek yol olur.
#
# 0.5.0 paketlenirken bu ayrim YOKTU: php-8.3'un yedek adresi 404 verdi ve denetim yayini BLOKE
# etti -- oysa kurulum saglamdi. Ayrimi yapmadan iki yanlistan birini secmek zorunda kaliyorduk:
# ya her ust yama yayini durdurur, ya da olu yedek yollar sessizce birikir. Artik ikisi de olmuyor:
# 1. sinif kirilirsa DURDURUR, 2. sinif kirilirsa AYRI ve BLOKE ETMEYEN uyari uretir (sessiz kalmaz).
#
# Hangi bilesenin canli cozumleyicisi oldugu ELLE LISTELENMEZ; ComponentBootstrapper kaynagindan
# TURETILIR (asagidaki Get-AdaLiveDownloadUrlResolution). Elle liste bir gun kodla ayrisir ve
# yanlis bilesen yanlis sinifa duser.

param(
    [switch]$SkipLargePackages,
    [switch]$KeepDownloads,
    # Yalnizca testler icin: fonksiyonlari tanimla, hicbir sey calistirma.
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manifestPath = Join-Path $repoRoot "components.json"
$bootstrapperPath = Join-Path $repoRoot "src\AdaPanel.Infrastructure\Bootstrap\ComponentBootstrapper.cs"
$downloadRoot = Join-Path $repoRoot ".tools\component-package-audit"
$reportPath = Join-Path $repoRoot "test-results\component-package-audit.json"

$zipMarkers = @{
    "php-8.3"       = @("php.exe", "php-cgi.exe")
    "mariadb-11"    = @("mariadbd.exe", "mariadb-install-db.exe", "mariadb-admin.exe", "mysql.exe", "mysqldump.exe")
    "mysql-8"       = @("mysqld.exe", "mysql.exe", "mysqladmin.exe", "mysqldump.exe")
    "phpmyadmin"    = @("index.php", "config.sample.inc.php")
    "redis"         = @("redis-server.exe", "redis-cli.exe")
    "memcached"     = @("memcached.exe")
    "nginx"         = @("nginx.exe", "conf/nginx.conf")
    "stalwart-cli"  = @("stalwart-cli.exe")
    "stalwart-mail" = @("stalwart.exe")
    "docker-engine" = @("docker.exe", "dockerd.exe")
    "win-acme"      = @("wacs.exe")
}

$largeComponents = @("mysql-8", "postgresql-16", "pgadmin4")

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $true
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan
$client.DefaultRequestHeaders.UserAgent.ParseAdd("AdaPanel-Package-Audit/1.0")

function Get-DownloadFileName {
    param([object]$Component)

    $uri = [Uri]$Component.downloadUrl
    $name = [IO.Path]::GetFileName($uri.LocalPath)
    if ([string]::IsNullOrWhiteSpace($name)) {
        return "$($Component.id).bin"
    }

    return $name
}

function Download-File {
    param(
        [string]$Url,
        [string]$Destination,
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 600
    )

    # ATOMIK INDIRME. Eskiden dogrudan $Destination'a yaziliyordu ve "dosya varsa gec" deniyordu;
    # yarida kopan bir indirme geride TAM dosya gorunumlu bir kalinti birakiyordu ve sonraki kosum
    # onu BOYUT/HASH BAKMADAN "indirilmis" sayiyordu. (0.5.0 paketlenirken vc-redist-x64 indirmesi
    # iki kez yarida koptu -- bu senaryo teorik degil.) Artik dosya once ".partial" adina yazilir,
    # YALNIZCA tam boyutta bittiginde hedef adina tasinir. Boylece $Destination'da bir dosya
    # bulunmasi "bu indirme TAMAMLANDI" demektir ve asagidaki erken donus guvenlidir.
    if (Test-Path -LiteralPath $Destination) {
        return
    }

    $partial = "$Destination.partial"
    if (Test-Path -LiteralPath $partial) {
        Remove-Item -LiteralPath $partial -Force
    }

    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    try {
        $expectedLength = $null
        $response = $client.GetAsync(
            $Url,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead,
            $deadline.Token
        ).GetAwaiter().GetResult()
        try {
            $response.EnsureSuccessStatusCode() | Out-Null
            $expectedLength = $response.Content.Headers.ContentLength
            $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try {
                $target = [IO.File]::Create($partial)
                try {
                    # ResponseHeadersRead does not bound the response body. A stalled CDN
                    # must not block the release indefinitely (including PowerShell 5.1).
                    $copyTask = $source.CopyToAsync($target, 81920, $deadline.Token)
                    $remaining = [Math]::Max(1, ($TimeoutSeconds * 1000) - [int]$elapsed.ElapsedMilliseconds)
                    if (-not $copyTask.Wait($remaining)) {
                        throw "Indirme zaman asimina ugradi ($TimeoutSeconds saniye): $Url"
                    }
                    $copyTask.GetAwaiter().GetResult() | Out-Null
                }
                finally {
                    $target.Dispose()
                }
            }
            finally {
                $source.Dispose()
            }
        }
        finally {
            $response.Dispose()
        }

        # Sunucu Content-Length bildirdiyse TAM o kadar bayt gelmis olmali. Akis sessizce erken
        # biterse (CDN kopmasi) .NET her zaman hata firlatmaz -- olculdu: PowerShell 5.1'in
        # HttpClient'i yarim govdeyi SESSIZCE tamamlanmis sayiyor. Olcum burada yapilir.
        $actualLength = (Get-Item -LiteralPath $partial).Length
        if ($null -ne $expectedLength -and $actualLength -ne [int64]$expectedLength) {
            throw "Indirme yarida kesildi: $Url (beklenen $expectedLength bayt, gelen $actualLength bayt)"
        }
        if ($actualLength -eq 0) {
            throw "Indirilen dosya bos: $Url"
        }

        Move-Item -LiteralPath $partial -Destination $Destination -Force
    }
    finally {
        $deadline.Dispose()
        # Basarisiz kosumun kalintisi ASLA birakilmaz: birakilirsa bir sonraki kosum onu
        # "tamamlanmis indirme" sanmaya devam eder.
        if (Test-Path -LiteralPath $partial) {
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        }
    }
}

<#
.SYNOPSIS
Girintiye dayali C# blok cikarici: baslik satirini bulur, ayni girintideki kapanis suslu parantezine
kadar olan metni dondurur.
.NOTES
Tam bir C# ayristiricisi degil; dosya tutarli bicimlendirildigi (dotnet format) icin yeterlidir.
Metin bulunamazsa $null doner ve CAGIRAN TARAF HATA VERIR -- sessizce bos liste uretmek, tum
bilesenleri yanlis sinifa dusurur.
#>
function Get-AdaIndentedBlock {
    param(
        [string]$Source,
        [string]$HeaderPattern
    )

    $header = [regex]::Match($Source, "(?m)^(?<girinti>[ ]+)$HeaderPattern")
    if (-not $header.Success) {
        return $null
    }

    $govde = $Source.Substring($header.Index)
    $kapanis = [regex]::Match($govde, "(?m)^$($header.Groups['girinti'].Value)\}")
    if (-not $kapanis.Success) {
        return $null
    }

    return $govde.Substring(0, $kapanis.Index + $kapanis.Length)
}

<#
.SYNOPSIS
Indirme adresini CANLI cozen bilesenleri ComponentBootstrapper kaynagindan turetir.
.DESCRIPTION
Kural (koddan okunur, elle yazilmaz): ResolveDynamicComponentAsync'teki bir "case" dali, indirme
adresini canli belirliyorsa ya kendi govdesinde ya da cagirdigi cozumleyicinin govdesinde
CopyComponent'e "downloadUrl:" verir.

Bu ayrim onemli:
  * php-8.3 / mariadb-11 / nginx ... -> adres canli uretilir, manifest YEDEKTIR.
  * composer / phpmyadmin           -> cozumleyici YALNIZCA sha256 tazeler, adres manifesttekidir.
  * docker-engine                   -> "return component" der, hicbir sey cozmez.
Son iki grubun manifest adresi kurulumda GERCEKTEN kullanilir; kirilirsa yayin durmalidir.

Ayrica ResolveDynamicComponentAsync'in switch'ten sonraki kuyrugu "php-<surum>" kimlikleri icin
genel bir cozumleyici tasir; oradan bir ON EK kurali turetilir (yine elle yazilmaz).
#>
function Get-AdaLiveDownloadUrlResolution {
    param([string]$SourcePath)

    if (-not (Test-Path -LiteralPath $SourcePath)) {
        throw "ComponentBootstrapper kaynagi bulunamadi: $SourcePath"
    }

    $source = Get-Content -LiteralPath $SourcePath -Raw
    $dagitim = Get-AdaIndentedBlock -Source $source `
        -HeaderPattern "(?:private|internal|public|protected)[^\r\n]*?\bResolveDynamicComponentAsync\s*\("
    if (-not $dagitim) {
        throw "ResolveDynamicComponentAsync bulunamadi: $SourcePath. Denetim, canli cozumlenen bilesenleri ayirt edemez."
    }

    $switchBloku = Get-AdaIndentedBlock -Source $dagitim -HeaderPattern "switch\s*\(normalizedId\)"
    if (-not $switchBloku) {
        throw "ResolveDynamicComponentAsync icinde 'switch (normalizedId)' bulunamadi: $SourcePath"
    }

    # Cozumleyici metot govdeleri (ad -> metin); ayni metot birden cok case'ten cagrilabilir.
    $govdeler = @{}

    $kimlikler = [System.Collections.Generic.List[string]]::new()
    $etiketler = @([regex]::Matches($switchBloku, '(?m)^\s*case\s+"(?<id>[^"]+)"\s*:'))
    for ($index = 0; $index -lt $etiketler.Count; $index++) {
        $etiket = $etiketler[$index]
        $baslangic = $etiket.Index + $etiket.Length
        $bitis = if ($index + 1 -lt $etiketler.Count) { $etiketler[$index + 1].Index } else { $switchBloku.Length }
        $govde = $switchBloku.Substring($baslangic, $bitis - $baslangic)

        $canli = $govde -match 'downloadUrl\s*:'
        if (-not $canli) {
            foreach ($cagri in [regex]::Matches($govde, '\b(?<ad>Resolve[A-Za-z0-9]*Async)\s*\(')) {
                $ad = $cagri.Groups["ad"].Value
                if (-not $govdeler.ContainsKey($ad)) {
                    $govdeler[$ad] = Get-AdaIndentedBlock -Source $source `
                        -HeaderPattern "(?:private|internal|public|protected)[^\r\n]*?\b$([regex]::Escape($ad))\s*\("
                }

                $cozumleyici = $govdeler[$ad]
                if ($cozumleyici -and $cozumleyici -match 'downloadUrl\s*:') {
                    $canli = $true
                    break
                }
            }
        }

        if ($canli) {
            $kimlikler.Add($etiket.Groups["id"].Value.Trim().ToLowerInvariant())
        }
    }

    # switch'ten sonraki kuyruk: "php-<dal>" gibi kimlik ON EKI ile calisan genel cozumleyici.
    $onEkler = [System.Collections.Generic.List[string]]::new()
    $kuyrukBaslangici = $dagitim.IndexOf($switchBloku) + $switchBloku.Length
    if ($kuyrukBaslangici -lt $dagitim.Length) {
        $kuyruk = $dagitim.Substring($kuyrukBaslangici)
        if ($kuyruk -match 'downloadUrl\s*:') {
            foreach ($eslesme in [regex]::Matches($kuyruk, 'Id\.StartsWith\("(?<prefix>[^"]+)"')) {
                $onEkler.Add($eslesme.Groups["prefix"].Value.ToLowerInvariant())
            }
        }
    }

    if ($kimlikler.Count -eq 0) {
        throw "ResolveDynamicComponentAsync'ten hicbir canli cozumleyici turetilemedi: $SourcePath. Ayristirma kurali kodla ayrismis olabilir."
    }

    return [pscustomobject]@{
        Ids      = @($kimlikler)
        Prefixes = @($onEkler)
    }
}

<#
.SYNOPSIS
Bir bilesenin indirme adresi canli mi cozuluyor (yani manifest adresi YEDEK mi)?
#>
function Test-AdaComponentHasLiveDownloadUrl {
    param(
        [string]$ComponentId,
        [object]$Resolution
    )

    $normal = $ComponentId.Trim().ToLowerInvariant()
    if ($Resolution.Ids -contains $normal) {
        return $true
    }

    foreach ($onEk in $Resolution.Prefixes) {
        if (-not $normal.StartsWith($onEk)) {
            continue
        }

        # C# tarafindaki koruma ile AYNI: on ekten sonrasi "<sayi>.<sayi>" olmali (php-8.3).
        if ($normal.Substring($onEk.Length) -match '^\d+\.\d+$') {
            return $true
        }
    }

    return $false
}

<#
.SYNOPSIS
Basarisiz bir bilesen denetimini SINIFLANDIRIR: yayini durduran hata mi, yoksa bayat yedek uyarisi mi?
#>
function New-AdaComponentAuditFailure {
    param(
        [string]$ComponentId,
        [string]$Message,
        [bool]$HasLiveDownloadUrl
    )

    if ($HasLiveDownloadUrl) {
        return [pscustomobject]@{
            id       = $ComponentId
            status   = "StaleFallback"
            blocking = $false
            detail   = "Manifest YEDEK adresi dogrulanamadi (kurulum canli cozumleyici kullanir): $Message"
        }
    }

    return [pscustomobject]@{
        id       = $ComponentId
        status   = "Failed"
        blocking = $true
        detail   = $Message
    }
}

function Resolve-ExpectedHash {
    param(
        [object]$Component
    )

    if (-not [string]::IsNullOrWhiteSpace($Component.sha256)) {
        return $Component.sha256.Trim().ToLowerInvariant()
    }

    $checksumUrl = switch ($Component.id) {
        "composer" { "https://getcomposer.org/download/latest-stable/composer.phar.sha256" }
        "phpmyadmin" { "$($Component.downloadUrl).sha256" }
        default { $null }
    }
    if (-not $checksumUrl) {
        return $null
    }

    $content = $client.GetStringAsync($checksumUrl).GetAwaiter().GetResult()
    $match = [regex]::Match($content, "(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])")
    if (-not $match.Success) {
        throw "SHA256 bulunamadi: $checksumUrl"
    }

    return $match.Value.ToLowerInvariant()
}

function Assert-ZipMarkers {
    param(
        [string]$ComponentId,
        [string]$Path
    )

    if (-not $zipMarkers.ContainsKey($ComponentId)) {
        return
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | ForEach-Object {
            $_.FullName.Replace("\", "/").TrimStart("/").ToLowerInvariant()
        })
        foreach ($marker in $zipMarkers[$ComponentId]) {
            $normalizedMarker = $marker.Replace("\", "/").ToLowerInvariant()
            if (-not ($entries | Where-Object {
                $_ -eq $normalizedMarker -or $_.EndsWith("/$normalizedMarker")
            })) {
                throw "$ComponentId ZIP isaret dosyasi eksik: $marker"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

if ($DefineFunctionsOnly) {
    return
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$results = [System.Collections.Generic.List[object]]::new()
$staleFallbacks = [System.Collections.Generic.List[string]]::new()
$liveResolution = Get-AdaLiveDownloadUrlResolution -SourcePath $bootstrapperPath

New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $reportPath -Parent) -Force | Out-Null

try {
    foreach ($component in $manifest.components) {
        if ($SkipLargePackages -and $largeComponents -contains $component.id) {
            $results.Add([pscustomobject]@{
                id = $component.id
                status = "Skipped"
                detail = "Large package"
            })
            continue
        }

        $hasLiveUrl = Test-AdaComponentHasLiveDownloadUrl -ComponentId $component.id -Resolution $liveResolution
        try {
            $fileName = Get-DownloadFileName $component
            $destination = Join-Path $downloadRoot "$($component.id)-$fileName"
            Write-Host "Denetleniyor: $($component.id)"
            Download-File $component.downloadUrl $destination

            $expectedHash = Resolve-ExpectedHash $component
            if ($expectedHash) {
                $actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actualHash -ne $expectedHash) {
                    throw "$($component.id) SHA256 uyusmazligi. Beklenen=$expectedHash Gercek=$actualHash"
                }
            }
            elseif ($component.type -in @("msi", "installer")) {
                $signature = Get-AuthenticodeSignature -LiteralPath $destination
                if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
                    throw "$($component.id) imza gecersiz: $($signature.Status)"
                }
            }
            else {
                throw "$($component.id) icin SHA256 veya Authenticode dogrulamasi yok."
            }

            if ($component.type -eq "zip") {
                Assert-ZipMarkers $component.id $destination
            }
            elseif ($component.id -eq "composer") {
                if ((Get-Item $destination).Length -lt 1MB) {
                    throw "Composer paketi beklenenden kucuk."
                }
            }
            elseif ($component.id -eq "adminer") {
                $prefix = [IO.File]::ReadAllText($destination)
                if (-not $prefix.StartsWith("<?php", [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Adminer PHP dosyasi gecersiz."
                }
            }

            $results.Add([pscustomobject]@{
                id = $component.id
                status = "Passed"
                liveDownloadUrl = $hasLiveUrl
                bytes = (Get-Item $destination).Length
                sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            })
        }
        catch {
            $kayit = New-AdaComponentAuditFailure `
                -ComponentId $component.id `
                -Message $_.Exception.Message `
                -HasLiveDownloadUrl $hasLiveUrl
            $results.Add($kayit)

            if ($kayit.blocking) {
                throw
            }

            # Kurulum bu bilesende canli cozumleyiciyi kullanir, o yuzden yayin DURMAZ. Ama yedek
            # yolun olu olmasi gercek bir risktir: sessiz gecilmez, kosum sonunda tekrar ozetlenir.
            $staleFallbacks.Add($kayit.detail)
            Write-Warning $kayit.detail
        }
    }
}
finally {
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding utf8
    $client.Dispose()
    $handler.Dispose()

    if (-not $KeepDownloads -and (Test-Path $downloadRoot)) {
        Remove-Item -LiteralPath $downloadRoot -Recurse -Force
    }
}

if ($staleFallbacks.Count -gt 0) {
    Write-Host ""
    Write-Warning "BAYAT YEDEK ADRES: $($staleFallbacks.Count) bilesenin manifest adresi dogrulanamadi. Kurulum canli cozumleme ile calisir, ama canli cozumleme basarisiz olursa DUSULECEK YER YOK. components.json guncellenmeli:"
    foreach ($uyari in $staleFallbacks) {
        Write-Warning "  - $uyari"
    }
    Write-Host ""
}

Write-Host "Paket denetimi tamamlandi: $reportPath"
