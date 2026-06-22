param(
    [string]$FrontendDist = (Join-Path $PSScriptRoot "..\\frontend\\dist"),
    [string]$ApiWwwRoot = (Join-Path $PSScriptRoot "..\\src\\AdaPanel.Api\\wwwroot")
)

$resolvedDist = Resolve-Path -Path $FrontendDist -ErrorAction Stop
$resolvedWwwRoot = Resolve-Path -Path $ApiWwwRoot -ErrorAction Stop

Get-ChildItem -Path $resolvedWwwRoot.Path -Force | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $resolvedDist.Path "*") -Destination $resolvedWwwRoot.Path -Recurse -Force

Write-Host "Frontend output synced to $($resolvedWwwRoot.Path)"
