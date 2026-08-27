# AdaPanel installer paketleme (npm typecheck/test adimlarini ATLAR).
# Frontend dist'in ve testlerin onceden gectigi varsayilir.
# Kullanim: powershell -File scripts\package-installer.ps1
#
# -Version VERILMEZSE surum Directory.Build.props'tan OKUNUR. Verilirse kaynakla AYNI olmalidir.
# Burada eskiden "0.2.28" varsayilani duruyordu ve hicbir script kaynak surumu okumuyordu; sonuc
# yanlis damgalanmis exe -> panelin surumu release tag'inin altina duser -> self-update her gun
# ayni paketi indirip kurar (sonsuz dongu). Bkz. scripts\version-guard.ps1.
param(
    [string]$Version,
    [switch]$SkipComponentAudit
)
$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

. (Join-Path $PSScriptRoot "version-guard.ps1")
$Version = Resolve-AdaReleaseVersion -RepoRoot $repo -RequestedVersion $Version
Write-Host "Surum (Directory.Build.props): $Version"
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

# .HTACCESS IIS MODULU -- AYRI DERLENIR, cunku net48 hedefler (IIS IHttpModule ve GAC bunu
# gerektiriyor); panelin kendisi net8'dir ve tek publish ikisini birden uretemez.
#
# NEDEN BU BLOK VAR: modul solution'da olduğu icin `dotnet test` onu derliyordu ve BUTUN TESTLER
# YESILDI -- ama publish yalnizca AdaPanel.Api.csproj'u aliyor, .iss'te de Files girdisi yoktu.
# Sonuc: .htaccess ozelligi pakete HIC girmedi. Kurulum, var OLMAYAN bir DLL'i gosteren bir
# betik calistiriyordu; ozellik 0.7.0-0.7.2'de olu olarak yayinlandi. Yesil test, teslim edilmis
# demek DEGILDIR -- teslim yolunun kendisi de olculmeliydi.
$moduleProject = Join-Path $repo "src\AdaPanel.HtaccessModule\AdaPanel.HtaccessModule.csproj"
$moduleOut = Join-Path $repo "artifacts\htaccess-module"
if (Test-Path $moduleOut) { Remove-Item $moduleOut -Recurse -Force }
& $dotnet build $moduleProject -c Release -o $moduleOut "/p:Version=$Version" --nologo
if ($LASTEXITCODE -ne 0) { throw ".htaccess modulu derlenemedi." }

$moduleDll = Join-Path $moduleOut "AdaPanel.HtaccessModule.dll"
if (-not (Test-Path $moduleDll)) { throw ".htaccess modulu uretilmedi: $moduleDll" }
Copy-Item $moduleDll (Join-Path $publishDir "AdaPanel.HtaccessModule.dll") -Force

# Paket icinde GERCEKTEN oldugunu dogrula -- bu satir olmasaydi ayni hata sessizce geri gelirdi.
$packagedModule = Join-Path $publishDir "AdaPanel.HtaccessModule.dll"
if (-not (Test-Path $packagedModule)) { throw ".htaccess modulu publish klasorune kopyalanamadi." }
Write-Host (".htaccess modulu paketlendi: {0:N0} bayt" -f (Get-Item $packagedModule).Length)

if ($SkipComponentAudit) {
    Write-Warning "Bilesen paketleri denetimi ATLANDI (-SkipComponentAudit). Yayinlanacak paket icin ATLAMAYIN."
} else {
    & (Join-Path $repo "scripts\audit-component-packages.ps1") -SkipLargePackages
    if ($LASTEXITCODE -ne 0) { throw "Bilesen paketleri denetimi basarisiz oldu." }
}

# Paketlenecek icerigin denetimi -- beklenmeyen ikili / bulasma imzasi varsa DUR.
& (Join-Path $repo "scripts\verify-publish-output.ps1") `
    -PublishDir $publishDir `
    -ManifestPath (Join-Path $repo "artifacts\publish-manifest.json")

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup (ISCC.exe) bulunamadi." }

& $iscc "/DMyAppVersion=$Version" (Join-Path $repo "installer\AdaPanel.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup derlemesi basarisiz." }

$exe = Join-Path $repo "artifacts\installer\AdaPanelSetup.exe"

# URETILEN PAKETIN KENDISININ DENETIMI -- ISCC'den SONRA olmak ZORUNDA.
# Yukaridaki publish denetimi ISCC'den ONCE kosar ve yalnizca publish klasorunu tarar; uretilen
# AdaPanelSetup.exe onun gormedigi tek artefakti. Bu makinedeki PE bulastiricisi diskte duran
# bitmis exe'leri yeniden bulastiriyordu ve bulasik bir installer ozeti tuttugu icin kurulu TUM
# panellere SYSTEM olarak dagitilirdi. -Version verildiginde surum damgasi da burada dogrulanir.
& (Join-Path $repo "scripts\verify-publish-output.ps1") `
    -InstallerPath $exe `
    -ExpectedVersion $Version `
    -ManifestPath (Join-Path $repo "artifacts\installer-manifest.json")

$f = Get-Item $exe
# ASAGIDAKI SATIRLAR BIR KAYITTIR, DOGRULAMA DEGIL. "Yayindaki dosyayi indir, yereldekiyle hash
# karsilastir" yontemi kaynaktaki bulasmaya KORDUR: yereldeki dosya zaten bulasiksa iki hash de
# birebir tutar. Gercek dogrulama yukaridaki verify-publish-output.ps1 adimindadir; icerik
# dokumu artifacts\publish-manifest.json dosyasindadir.
Write-Host ("Version: {0}  (release tag'i BUNUNLA AYNI olmali)" -f $Version)
Write-Host ("Size  : {0:N0} byte ({1:N2} MiB)" -f $f.Length, ($f.Length / 1MB))
Write-Host ("SHA256: {0}" -f (Get-FileHash $exe -Algorithm SHA256).Hash)
Write-Host ("Last  : {0}" -f $f.LastWriteTime)
