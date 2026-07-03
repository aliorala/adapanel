param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    # Panelin RAPOR ETTIGI surum. Self-update icin GitHub release tag'i ile AYNI olmali
    # (ornek: -Version 0.2.5 ile build et -> release'i "0.2.5" tag'iyle yayinla).
    [string]$Version = "0.2.6"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
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
