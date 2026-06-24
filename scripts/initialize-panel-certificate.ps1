param(
    [string]$DataRoot = "$env:ProgramData\\AdaPanel"
)

$ErrorActionPreference = "Stop"

$resolvedDataRoot = [Environment]::ExpandEnvironmentVariables($DataRoot)
$certDirectory = Join-Path $resolvedDataRoot "certs"
$certificatePath = Join-Path $certDirectory "panel.pfx"
$passwordPath = Join-Path $certDirectory "panel.password"

New-Item -ItemType Directory -Path $certDirectory -Force | Out-Null

# certs klasörü önceki sürümün sertleştirmesinden bozuk/kısıtlı ACL almış olabilir; panel.pfx'i yazabilmek
# için erişimi geri ver (best-effort). Panel servisi açılışta sertleştirmeyi güvenli biçimde yeniden uygular.
try { & icacls "$certDirectory" /reset /T /C 2>$null | Out-Null } catch { }

$dnsNames = @("localhost", $env:COMPUTERNAME) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
$storeLocation = "LocalMachine"
$existingCertificate = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -eq "AdaPanel Self Signed" } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $existingCertificate) {
    try {
        $existingCertificate = New-SelfSignedCertificate `
            -DnsName $dnsNames `
            -CertStoreLocation "Cert:\LocalMachine\My" `
            -FriendlyName "AdaPanel Self Signed" `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -NotAfter (Get-Date).AddYears(3) `
            -ErrorAction Stop
    }
    catch {
        $storeLocation = "CurrentUser"
        $existingCertificate = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
            Where-Object { $_.FriendlyName -eq "AdaPanel Self Signed" } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1

        if (-not $existingCertificate) {
            $existingCertificate = New-SelfSignedCertificate `
                -DnsName $dnsNames `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -FriendlyName "AdaPanel Self Signed" `
                -KeyAlgorithm RSA `
                -KeyLength 2048 `
                -HashAlgorithm SHA256 `
                -NotAfter (Get-Date).AddYears(3)
        }
    }
}

$rootScopePath = if ($storeLocation -eq "LocalMachine") { "Cert:\LocalMachine\Root" } else { "Cert:\CurrentUser\Root" }
$rootStoreScope = if ($storeLocation -eq "LocalMachine") { "LocalMachine" } else { "CurrentUser" }
$trustedRootCertificate = Get-ChildItem $rootScopePath -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $existingCertificate.Thumbprint } |
    Select-Object -First 1

if (-not $trustedRootCertificate) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", $rootStoreScope)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $store.Add($existingCertificate)
    }
    finally {
        $store.Close()
    }
}

$passwordCharacters = 48..57 + 65..90 + 97..122
$password = -join ($passwordCharacters | Get-Random -Count 32 | ForEach-Object { [char]$_ })
$securePassword = ConvertTo-SecureString $password -AsPlainText -Force

if (Test-Path $certificatePath) {
    Remove-Item $certificatePath -Force -ErrorAction SilentlyContinue
}

Export-PfxCertificate -Cert $existingCertificate -FilePath $certificatePath -Password $securePassword | Out-Null
Set-Content -Path $passwordPath -Value $password -NoNewline

Write-Host "AdaPanel self-signed certificate exported to $certificatePath."
