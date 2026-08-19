param(
    [string]$DataRoot = "$env:ProgramData\\AdaPanel",

    # Testler icin: yalnizca fonksiyonlari tanimla, sertifika uretme/yazma akisini calistirma.
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = "Stop"

# =======================================================================================
# K3 - certs KLASORUNUN ACL'i (kok neden duzeltmesi)
# =======================================================================================
# ESKI DAVRANIS: bu betik her kurulumda `icacls "<certs>" /reset /T /C` calistiriyordu. /reset
# MIRASI GERI ACAR; ProgramData'dan gelen `BUILTIN\Users : ReadAndExecute` yeniden dusar ve panelin
# acilista uyguladigi K1 sertlestirmesini KURULUM BASINA IPTAL EDER. Olculdu: panel.password (32
# bayt, sertifikanin ozel anahtar parolasi) YUKSELTILMEMIS bir kullanici olarak okunabiliyordu --
# yaninda panel.pfx ile birlikte, yani panelin TLS kimligi kiraciya aciktı.
#
# YENI DAVRANIS: erisim "hepsini ac" ile degil, DOGRU ACL'i YENIDEN KURARAK garanti edilir.
# Miras kapatilir ve KOPYALANMAZ; DACL sifirdan yalnizca SYSTEM + Administrators + bu betigi
# calistiran hesap ile kurulur (panel servisi SYSTEM'dir, installer Administrator'dur -> ikisi de
# yazabilir; kiraci hicbir sekilde okuyamaz). Panel servisi acilista ayni sonucu uygular
# (DataRootHardeningService.BuildSensitiveDirectorySecurity) -- iki taraf artik CAKISMAZ.
function Set-AdaPanelSecureAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) { return $false }

    try {
        $isDirectory = (Get-Item -LiteralPath $Path -Force).PSIsContainer
        $acl = if ($isDirectory) {
            New-Object System.Security.AccessControl.DirectorySecurity
        }
        else {
            New-Object System.Security.AccessControl.FileSecurity
        }

        # Miras KAPALI ve KOPYALANMAZ: kokte ne varsa (olculdu: Everyone=FullControl) burada olur.
        $acl.SetAccessRuleProtection($true, $false)

        $fullCtrl = [System.Security.AccessControl.FileSystemRights]::FullControl
        $allow    = [System.Security.AccessControl.AccessControlType]::Allow
        $inherit  = if ($isDirectory) {
            [System.Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
        }
        else {
            [System.Security.AccessControl.InheritanceFlags]::None
        }
        $noProp   = [System.Security.AccessControl.PropagationFlags]::None

        $owners = @(
            (New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)),
            (New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null))
        )
        $current = ([Security.Principal.WindowsIdentity]::GetCurrent()).User
        if ($current -and ($owners -notcontains $current)) { $owners += $current }

        foreach ($owner in $owners) {
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($owner, $fullCtrl, $inherit, $noProp, $allow)))
        }

        Set-Acl -LiteralPath $Path -AclObject $acl
        return $true
    }
    catch {
        Write-Warning "ACL uygulanamadi ('$Path'): $($_.Exception.Message)"
        return $false
    }
}

if ($DefineFunctionsOnly) {
    return
}

$resolvedDataRoot = [Environment]::ExpandEnvironmentVariables($DataRoot)
$certDirectory = Join-Path $resolvedDataRoot "certs"
$certificatePath = Join-Path $certDirectory "panel.pfx"
$passwordPath = Join-Path $certDirectory "panel.password"

New-Item -ItemType Directory -Path $certDirectory -Force | Out-Null

# certs klasoru onceki surumun sertlestirmesinden bozuk/kisitli ACL almis olabilir; panel.pfx'i
# yazabilmek icin erisimi DOGRU ACL'i yeniden kurarak garanti et (eskiden /reset ile aciliyordu).
[void](Set-AdaPanelSecureAcl -Path $certDirectory)

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

# K3 - PAROLA DOSYASI: Kestrel sertifikayi acilista okumak zorunda oldugu icin parola makinede
# cozulebilir durumda kalmali; DPAPI(LocalMachine) ile sifrelemek AYNI makinedeki her surece yine
# cozdurur, yani tek gercek kontrol DOSYA IZNIDIR. Bu yuzden parola ve .pfx dosyalarinin DACL'i
# tek tek de kilitlenir: klasor izni herhangi bir sebeple gevserse (baska bir betik, elle mudahale)
# dosyalar KENDI baslarina korunmus kalir. Miras kapali + yalniz SYSTEM/Administrators/bu hesap.
[void](Set-AdaPanelSecureAcl -Path $certificatePath)
[void](Set-AdaPanelSecureAcl -Path $passwordPath)

Write-Host "AdaPanel self-signed certificate exported to $certificatePath."
