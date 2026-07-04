# AdaPanel installer paketleme (npm typecheck/test adimlarini ATLAR).
# Frontend dist'in ve testlerin onceden gectigi varsayilir.
# Kullanim: powershell -File scripts\package-installer.ps1 [-Version 0.2.4]
# -Version: panelin RAPOR ETTIGI surum. Self-update icin GitHub release tag'i ile AYNI olmali
# (ornek: -Version 0.2.5 ile paketle -> release'i "0.2.5" tag'iyle yayinla). Yoksa panel surumu
# release tag'iyle esitlenmez ve "guncelleme var" uyarisi hic kaybolmaz.
param([string]$Version = "0.2.16")
$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDir = Join-Path $repo "artifacts\publish\win-x64"
$dotnet = Join-Path $repo ".tools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet).Source }

& (Join-Path $repo "scripts\sync-frontend.ps1") | Out-Null

if (Test-Path $publishDir) { Get-ChildItem $publishDir -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

& $dotnet publish (Join-Path $repo "src\AdaPanel.Api\AdaPanel.Api.csproj") `
    -c Release -r win-x64 --self-contained true -o $publishDir `
    "/p:Version=$Version" "/p:InformationalVersion=$Version" /p:IncludeSourceRevisionInInformationalVersion=false `
    /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:DebugType=None /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Publish basarisiz." }

Copy-Item (Join-Path $repo "components.json") (Join-Path $publishDir "components.json") -Force

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup (ISCC.exe) bulunamadi." }

& $iscc "/DMyAppVersion=$Version" (Join-Path $repo "installer\AdaPanel.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup derlemesi basarisiz." }

$exe = Join-Path $repo "artifacts\installer\AdaPanelSetup.exe"
$f = Get-Item $exe
Write-Host ("Version: {0}  (release tag'i BUNUNLA AYNI olmali)" -f $Version)
Write-Host ("Size  : {0:N0} byte ({1:N2} MiB)" -f $f.Length, ($f.Length / 1MB))
Write-Host ("SHA256: {0}" -f (Get-FileHash $exe -Algorithm SHA256).Hash)
Write-Host ("Last  : {0}" -f $f.LastWriteTime)
