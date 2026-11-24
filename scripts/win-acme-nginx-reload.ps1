# Optional win-acme installation-script hook for an externally managed TLS Nginx.
# Install in an administrator-owned directory; see win-acme-nginx-reload.md.
[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$Prefix,
    [string]$ConfigurationPath,
    [string]$PidPath,
    [switch]$DefineFunctionsOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AdaNginxCurrentSid {
    return [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}

function Get-AdaNginxMasterIdentity {
    param([int]$MasterId, [string]$ExpectedExecutable)
    $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $MasterId" -ErrorAction Stop
    if ($null -eq $process -or [string]::IsNullOrWhiteSpace($process.ExecutablePath) -or
        -not [string]::Equals([IO.Path]::GetFullPath($process.ExecutablePath), $ExpectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Nginx master executable identity could not be verified.'
    }
    $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -ErrorAction Stop
    if ($owner.ReturnValue -ne 0 -or [string]::IsNullOrWhiteSpace($owner.Sid) -or
        $owner.Sid -ne (Get-AdaNginxCurrentSid) -or $null -eq $process.CreationDate) {
        throw 'Nginx master owner must match the account running this hook.'
    }
    return [string]$process.CreationDate.ToUniversalTime().Ticks
}

function Invoke-AdaNginxCommand {
    param([string]$Executable, [string]$WorkingDirectory, [string]$Arguments)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.WorkingDirectory = $WorkingDirectory
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $command = New-Object Diagnostics.Process
    $command.StartInfo = $start
    try {
        if (-not $command.Start()) { throw 'Nginx command could not start.' }
        $stdout = $command.StandardOutput.ReadToEndAsync()
        $stderr = $command.StandardError.ReadToEndAsync()
        if (-not $command.WaitForExit(30000)) {
            try { $command.Kill() } catch { }
            throw 'Nginx command timed out.'
        }
        # Drain redirected output without publishing configuration/certificate details.
        $output = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        if ($command.ExitCode -ne 0) { throw 'Nginx command failed; inspect the Nginx error log.' }
        return $output
    }
    finally { $command.Dispose() }
}

function Get-AdaNginxEffectivePidPath {
    param([string]$Dump, [string]$InstallPrefix)
    if ([string]::IsNullOrWhiteSpace($Dump) -or $Dump.Length -gt 2097152 -or $Dump -notmatch '(?m)^# configuration file .+:\r?$') {
        throw 'Nginx effective configuration could not be read.'
    }
    # Preserve escapes in unrelated directives (for example location ~ \.php$).
    # Only interpret a single, unambiguous value of a top-level pid directive.
    $tokens = New-Object 'Collections.Generic.List[string]'
    $pattern = '\G(?:\s+|#[^\r\n]*(?:\r?\n|$)|(?<token>[{};]|(?:"(?:\\[\s\S]|[^"\\])*"|''(?:\\[\s\S]|[^''\\])*''|\\[\s\S]|[^\s{};#"''\\])+))'
    $lexer = New-Object Text.RegularExpressions.Regex($pattern, [Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(2))
    $offset = 0
    while ($offset -lt $Dump.Length) {
        $match = $lexer.Match($Dump, $offset)
        if (-not $match.Success -or $match.Index -ne $offset) {
            throw 'Nginx effective configuration contains unsupported syntax.'
        }
        if ($match.Groups['token'].Success) { $tokens.Add($match.Groups['token'].Value) }
        $offset += $match.Length
    }
    $found = @()
    $depth = 0
    $directiveStart = $true
    for ($index = 0; $index -lt $tokens.Count; $index++) {
        $token = $tokens[$index]
        if ($token -eq '{') { $depth++; $directiveStart = $true; continue }
        if ($token -eq '}') {
            $depth--
            if ($depth -lt 0) { throw 'Nginx effective configuration is unbalanced.' }
            $directiveStart = $true
            continue
        }
        if ($token -eq ';') { $directiveStart = $true; continue }
        if ($depth -eq 0 -and $directiveStart -and $token.Trim('"', "'") -eq 'pid') {
            if ($index + 2 -ge $tokens.Count -or $tokens[$index + 2] -ne ';') {
                throw 'Nginx PID directive is ambiguous.'
            }
            $value = $tokens[$index + 1]
            if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
                ($value.StartsWith("'") -and $value.EndsWith("'"))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            if ($value -match '[\\$"''{};\r\n]' -or [string]::IsNullOrWhiteSpace($value)) {
                throw 'Nginx PID directive is unsupported.'
            }
            $found += $value
        }
        elseif ($depth -eq 0 -and $directiveStart -and ($token -replace '[\\"'']', '') -eq 'pid') {
            # Do not silently miss an escaped/concatenated spelling of pid.
            throw 'Nginx PID directive name is ambiguous.'
        }
        $directiveStart = $false
    }
    if ($depth -ne 0) { throw 'Nginx effective configuration is unbalanced.' }
    if ($found.Count -gt 1) { throw 'Nginx PID directive is ambiguous.' }
    $configured = 'logs/nginx.pid'
    if ($found.Count -eq 1) { $configured = $found[0] }
    if ([string]::IsNullOrWhiteSpace($configured) -or $configured.Contains('$')) {
        throw 'Nginx PID directive is unsupported.'
    }
    if (-not [IO.Path]::IsPathRooted($configured)) { $configured = Join-Path $InstallPrefix $configured }
    return [IO.Path]::GetFullPath($configured)
}

function Invoke-AdaNginxRenewalReload {
    param([string]$Executable, [string]$InstallPrefix, [string]$ConfigFile, [string]$PidFile)
    foreach ($path in @($Executable, $InstallPrefix, $ConfigFile, $PidFile)) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not [IO.Path]::IsPathRooted($path) -or
            $path -match '["\r\n]' -or $path -notmatch '^[A-Za-z]:[\\/]') {
            throw 'Explicit absolute local paths are required.'
        }
    }
    $executable = [IO.Path]::GetFullPath($Executable)
    $installPrefix = [IO.Path]::GetFullPath($InstallPrefix)
    $configFile = [IO.Path]::GetFullPath($ConfigFile)
    $pidFile = [IO.Path]::GetFullPath($PidFile)
    if (-not [IO.File]::Exists($executable) -or -not [IO.Directory]::Exists($installPrefix) -or
        -not [IO.File]::Exists($configFile) -or -not [IO.File]::Exists($pidFile)) {
        throw 'Nginx executable, prefix, configuration and master PID file must exist.'
    }
    $masterId = 0
    if (-not [int]::TryParse([IO.File]::ReadAllText($pidFile).Trim(), [ref]$masterId) -or $masterId -le 0) {
        throw 'Nginx master PID is invalid.'
    }
    $identity = Get-AdaNginxMasterIdentity $masterId $executable
    # Forward slashes avoid Windows trailing-backslash/closing-quote ambiguity.
    $prefixArgument = $installPrefix.Replace('\', '/').TrimEnd('/') + '/'
    $configArgument = $configFile.Replace('\', '/')
    $arguments = '-p "' + $prefixArgument + '" -c "' + $configArgument + '"'
    $dump = Invoke-AdaNginxCommand $executable $installPrefix ('-t -T ' + $arguments)
    $effectivePid = Get-AdaNginxEffectivePidPath $dump $installPrefix
    if (-not [string]::Equals($effectivePid, $pidFile, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Nginx effective PID path does not match the verified master PID file.'
    }
    # Do not signal a replaced/reused PID after validation.
    if ([IO.File]::ReadAllText($pidFile).Trim() -ne [string]$masterId -or
        (Get-AdaNginxMasterIdentity $masterId $executable) -ne $identity) {
        throw 'Nginx master changed during configuration validation.'
    }
    $null = Invoke-AdaNginxCommand $executable $installPrefix ($arguments + ' -s reload')
    Write-Output 'Nginx configuration test and graceful reload signal completed.'
}

if ($DefineFunctionsOnly) { return }
try {
    # win-acme can run as SYSTEM: only administrators/SYSTEM may own or write this hook.
    $acl = Get-Acl -LiteralPath $PSCommandPath
    $trusted = @('S-1-5-18', 'S-1-5-32-544')
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) {
        throw 'Install the hook with Administrators or SYSTEM ownership.'
    }
    $writeRights = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, ChangePermissions, TakeOwnership'
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $writeRights) -ne 0 -and
            $rule.IdentityReference.Value -notin $trusted) {
            throw 'The hook is writable by an account other than Administrators/SYSTEM.'
        }
    }
    Invoke-AdaNginxRenewalReload $ExecutablePath $Prefix $ConfigurationPath $PidPath
    exit 0
}
catch {
    [Console]::Error.WriteLine('Nginx renewal reload failed: ' + $_.Exception.Message)
    exit 1
}
