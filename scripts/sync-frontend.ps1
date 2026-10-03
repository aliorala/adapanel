param(
    [string]$FrontendDist = (Join-Path $PSScriptRoot "..\\frontend\\dist"),
    [string]$ApiWwwRoot = (Join-Path $PSScriptRoot "..\\src\\AdaPanel.Api\\wwwroot")
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\workspace-path.ps1')
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$resolvedDist = Resolve-Path -LiteralPath $FrontendDist -ErrorAction Stop
$resolvedWwwRoot = Assert-AdaWorkspaceChildPath -RepoRoot $repo -Path $ApiWwwRoot
if (-not (Test-Path -LiteralPath (Join-Path $resolvedDist.Path 'index.html') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $resolvedDist.Path 'assets') -PathType Container) -or
    @(Get-ChildItem -LiteralPath (Join-Path $resolvedDist.Path 'assets') -File -ErrorAction SilentlyContinue).Count -eq 0) {
    throw 'Frontend dist/index.html or assets are missing; existing output was preserved.'
}
if ($resolvedDist.Path -eq $resolvedWwwRoot -or
    $resolvedDist.Path.StartsWith($resolvedWwwRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedWwwRoot.StartsWith($resolvedDist.Path + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Frontend source and destination must be separate directories.'
}

New-Item -ItemType Directory -Path $resolvedWwwRoot -Force | Out-Null
Get-ChildItem -LiteralPath $resolvedWwwRoot -Force | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
Copy-Item -Path (Join-Path $resolvedDist.Path "*") -Destination $resolvedWwwRoot -Recurse -Force

Write-Host "Frontend output synced to $resolvedWwwRoot"
