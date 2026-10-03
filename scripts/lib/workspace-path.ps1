# Recursive cleanup must stay below the intended workspace and must not traverse a junction.
function Assert-AdaWorkspaceChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $root = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
    $target = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not $target.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Cleanup path must be a child of the workspace: $target"
    }
    $cursor = $target
    while ($cursor.Length -ge $root.Length) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Cleanup path must not traverse a reparse point: $cursor"
            }
        }
        if ($cursor -eq $root) { break }
        $cursor = [System.IO.Path]::GetDirectoryName($cursor)
    }
    return $target
}
