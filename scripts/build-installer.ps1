param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    # Panelin RAPOR ETTIGI surum. VARSAYILANI YOK: surum Directory.Build.props'tan OKUNUR.
    #
    # Onceden burada "0.2.28" yaziyordu ve hicbir script Directory.Build.props'u okumuyordu.
    # README'deki komut da parametresizdi -> kaynak 0.4.22 iken exe 0.2.28 damgalaniyor, release
    # 0.4.23 tag'iyle cikiyor, kurulu paneller indirip kuruyor ve 0.2.28'e DUSUYOR, sonraki
    # kontrolde yine 0.4.23 goruyor: her gun indirip kuran SONSUZ GUNCELLEME DONGUSU.
    # Verilirse kaynakla AYNI olmak zorundadir (bkz. scripts\version-guard.ps1).
    [string]$Version,
    # Bilesen paketleri denetimi AG ERISIMI ister ve buyuk indirmeler yapar. Cevrimdisi derlemede
    # bu bayrakla atlanabilir -- ama atlandigi EKRANA YAZILIR, sessizce gecilmez.
    [switch]$SkipComponentAudit
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# SURUM TEK KAYNAKTAN. Tutarsizsa burada durur; yanlis damgalanmis paket URETILMEZ.
. (Join-Path $PSScriptRoot "version-guard.ps1")
$Version = Resolve-AdaReleaseVersion -RepoRoot $repoRoot -RequestedVersion $Version
Write-Host "Surum (Directory.Build.props): $Version"
$frontendDir = Join-Path $repoRoot "frontend"
$publishDir = Join-Path $repoRoot "artifacts\publish\$RuntimeIdentifier"
$installerOutDir = Join-Path $repoRoot "artifacts\installer"
$localDotnet = Join-Path $repoRoot ".tools\dotnet\dotnet.exe"

if (Test-Path $localDotnet) {
    $dotnetExe = $localDotnet
} else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) {
        throw "dotnet bulunamadi. Once .NET 8 SDK kurun veya .tools\\dotnet\\dotnet.exe ekleyin."
    }

    $dotnetExe = $dotnetCommand.Source
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutDir -Force | Out-Null

Get-ChildItem -Path $publishDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

Push-Location $frontendDir
try {
    npm run typecheck
    if ($LASTEXITCODE -ne 0) {
        throw "Frontend typecheck basarisiz oldu."
    }

    npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "Frontend build basarisiz oldu."
    }
} finally {
    Pop-Location
}

& (Join-Path $repoRoot "scripts\sync-frontend.ps1")

& $dotnetExe test (Join-Path $repoRoot "AdaPanel.sln") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Testler basarisiz oldu."
}

& $dotnetExe publish (Join-Path $repoRoot "src\AdaPanel.Api\AdaPanel.Api.csproj") `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o $publishDir `
    "/p:Version=$Version" `
    "/p:InformationalVersion=$Version" `
    /p:IncludeSourceRevisionInInformationalVersion=false `
    /p:PublishSingleFile=true `
    /p:EnableCompressionInSingleFile=true `
    /p:DebugType=None `
    /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained publish basarisiz oldu."
}

Copy-Item (Join-Path $repoRoot "components.json") (Join-Path $publishDir "components.json") -Force

# BILESEN PAKETLERI DENETIMI. Bu betik VARDI ama hicbir yerden CAGRILMIYORDU: indirilecek
# php/mariadb/nginx... paketlerinin SHA256'sini ve imzasini dogruluyor, yani musteri sunucusuna
# inecek ikilileri denetliyor. Cagrilmayan bir denetim, olmayan bir denetimdir.
if ($SkipComponentAudit) {
    Write-Warning "Bilesen paketleri denetimi ATLANDI (-SkipComponentAudit). Yayinlanacak paket icin ATLAMAYIN."
} else {
    & (Join-Path $repoRoot "scripts\audit-component-packages.ps1") -SkipLargePackages
    if ($LASTEXITCODE -ne 0) {
        throw "Bilesen paketleri denetimi basarisiz oldu."
    }
}

# PAKETLENECEK ICERIGIN DENETIMI: beklenmeyen bir calistirilabilir ya da bulasma imzasi varsa
# burada durur. Boyut/SHA yazdirmak bir KAYITTIR; bu bir DOGRULAMADIR.
& (Join-Path $repoRoot "scripts\verify-publish-output.ps1") `
    -PublishDir $publishDir `
    -ManifestPath (Join-Path $repoRoot "artifacts\publish-manifest.json")

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)

$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup bulunamadi. Self-contained publish hazirlandi fakat AdaPanelSetup.exe uretilmedi."
    Write-Host "Publish klasoru: $publishDir"
    exit 0
}

& $iscc "/DMyAppVersion=$Version" (Join-Path $repoRoot "installer\AdaPanel.iss")
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup derlemesi basarisiz oldu."
}

Write-Host "AdaPanelSetup.exe olusturuldu (surum $Version). Cikti klasoru: $installerOutDir"
Write-Host "ONEMLI: GitHub release tag'ini '$Version' yap (yoksa self-update surumu esitleyemez)."
