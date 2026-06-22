param(
    [switch]$SkipLargePackages,
    [switch]$KeepDownloads
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manifestPath = Join-Path $repoRoot "components.json"
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
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$results = [System.Collections.Generic.List[object]]::new()

New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $reportPath -Parent) -Force | Out-Null

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
        [string]$Destination
    )

    if (Test-Path $Destination) {
        return
    }

    $response = $client.GetAsync(
        $Url,
        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
    ).GetAwaiter().GetResult()
    try {
        $response.EnsureSuccessStatusCode() | Out-Null
        $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        try {
            $target = [IO.File]::Create($Destination)
            try {
                $source.CopyTo($target)
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
            bytes = (Get-Item $destination).Length
            sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        })
    }
}
catch {
    $results.Add([pscustomobject]@{
        id = $component.id
        status = "Failed"
        detail = $_.Exception.Message
    })
    throw
}
finally {
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding utf8
    $client.Dispose()
    $handler.Dispose()

    if (-not $KeepDownloads -and (Test-Path $downloadRoot)) {
        Remove-Item -LiteralPath $downloadRoot -Recurse -Force
    }
}

Write-Host "Paket denetimi tamamlandi: $reportPath"
