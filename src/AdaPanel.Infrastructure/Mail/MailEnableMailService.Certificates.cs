using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailEnableMailService
{
    internal const string MailEnableCertificateStatePath = "config/mailenable-certificates.json";
    private const string CertificateJournalPath = "config/mailenable-certificate-pending.json";
    internal Func<string, MailCertificateMaterial?> StoreCertificateLookup { get; init; } = MailCertificateStore.Find;
    internal Func<string, string, string, string> ValidateCertificateMaterial { get; init; } = MailCertificateValidation.Validate;
    internal Func<string, string, string, bool, string, CancellationToken, Task<bool>>? CertificateIssuer { get; init; }

    internal sealed record ManagedCertificate(string Domain, string HostName, string Thumbprint, string PemDirectory);
    internal sealed record CertificateSnapshot(string DefaultName, string Sni, string[] Certificates, string[] RunningServices,
        Dictionary<string, string>? FriendlyNames = null);
    internal sealed record CertificateTransaction(string Thumbprint, string DefaultName, string Sni,
        string[] Removed, string[] RunningServices, bool AlreadyPresent, string AppliedDefaultName, string[] InventoryThumbprints, bool Started = false);

    private JsonFileStore CertificateFiles => fileStore ?? throw new AppException("mail.settings_unmeasured", 503, 503);

    public async Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default)
    {
        var domain = ValidateDomain(request.Domain);
        var host = ValidateDomain(string.IsNullOrWhiteSpace(request.HostName) ? "mail." + domain : request.HostName);
        using var mutation = await AcquireMutationAsync(cancellationToken);
        await RecoverCertificateTransactionAsync(cancellationToken);
        var material = StoreCertificateLookup(host);
        var pemDirectory = "";
        if (material is not null)
        {
            var tracked = await CertificateFiles.ReadAsync<ManagedCertificate[]>(MailEnableCertificateStatePath, [], cancellationToken);
            pemDirectory = tracked.FirstOrDefault(s => s.HostName.Equals(host, StringComparison.OrdinalIgnoreCase)
                && s.Thumbprint.Equals(material.Thumbprint, StringComparison.OrdinalIgnoreCase))?.PemDirectory ?? "";
        }
        if (material is null)
        {
            if (request.ReuseExistingOnly) throw new AppException("mail.certificate_store_missing", 409, 409);
            var contact = request.ContactEmail?.Trim() ?? "";
            if (contact.Length == 0)
                contact = (await CertificateFiles.ReadAsync<AcmeContactSettings?>(AcmeContactSettings.Path, null, cancellationToken))?.Email ?? "";
            pemDirectory = CertificateFiles.EnsureDirectory($"mailenable-certs/{host}/{Guid.NewGuid():N}");
            if (OperatingSystem.IsWindows())
                new DirectoryInfo(pemDirectory).SetAccessControl(Bootstrap.DataRootHardeningService.BuildSensitiveDirectorySecurity());
            var issuer = CertificateIssuer;
            if (issuer is null && certificateIssuerService is not null) issuer = certificateIssuerService.TryIssueWithWinAcmeAsync;
            if (issuer is null || !await issuer(domain, host, contact, request.UseStaging, pemDirectory, cancellationToken))
                throw new AppException("mail.certificate_acme_failed", 502, 502);
            var pair = MailService.ReadPemPair(pemDirectory);
            using var leaf = X509Certificate2.CreateFromPem(pair.CertificatePem, pair.PrivateKeyPem);
            material = new(pair.CertificatePem, pair.PrivateKeyPem, leaf.Thumbprint);
            if (contact.Length > 0)
                await CertificateFiles.WriteAsync(AcmeContactSettings.Path, new AcmeContactSettings { Email = contact }, cancellationToken);
        }
        return await ApplyMailEnableCertificateAsync(new(domain, host, material.Thumbprint, pemDirectory), material, null, cancellationToken);
    }

    public Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default) => SyncMailEnableCertificatesAsync(null, cancellationToken);
    public Task<MailCertificateDto?> SyncCertificateAsync(Func<CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) =>
        SyncMailEnableCertificatesAsync(beforeMutation, cancellationToken);

    private async Task<MailCertificateDto?> SyncMailEnableCertificatesAsync(Func<CancellationToken, Task>? beforeMutation, CancellationToken token)
    {
        using var mutation = await AcquireMutationAsync(token);
        if (beforeMutation is not null) await beforeMutation(token);
        await RecoverCertificateTransactionAsync(token);
        var managed = await CertificateFiles.ReadAsync<ManagedCertificate[]>(MailEnableCertificateStatePath, [], token);
        MailCertificateDto? changed = null;
        foreach (var item in managed)
        {
            MailCertificateMaterial? material;
            if (item.PemDirectory.Length > 0)
            {
                var pair = MailService.ReadPemPair(item.PemDirectory);
                using var leaf = X509Certificate2.CreateFromPem(pair.CertificatePem, pair.PrivateKeyPem);
                material = new(pair.CertificatePem, pair.PrivateKeyPem, leaf.Thumbprint);
            }
            else material = StoreCertificateLookup(item.HostName);
            if (material is null) throw new AppException("mail.certificate_store_missing", 409, 409);
            var current = await ProbeManagedCertificateAsync(item.HostName, material.Thumbprint, token);
            if (material.Thumbprint == item.Thumbprint && current.Active) continue;
            changed = await ApplyMailEnableCertificateAsync(item with { Thumbprint = material.Thumbprint }, material, beforeMutation, token);
        }
        return changed;
    }

    public async Task SyncWebCertificateAsync(string hostName, Func<CancellationToken, Task>? beforeMutation = null, CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        var managed = await CertificateFiles.ReadAsync<ManagedCertificate[]>(MailEnableCertificateStatePath, [], cancellationToken);
        var targets = managed.Where(item => MailService.CertificateNameMatches(hostName, item.HostName)).ToArray();
        if (targets.Length == 0 && hostName.StartsWith("mail.", StringComparison.OrdinalIgnoreCase))
        {
            var host = ValidateDomain(hostName);
            var settings = await ReadPlatformAsync(["smtpHostName", "smtpLocalDomainName"], cancellationToken);
            var greeting = ResolveGreeting(settings.GetValueOrDefault("smtpHostName", ""), settings.GetValueOrDefault("smtpLocalDomainName", ""));
            var workspace = await GetWorkspaceAsync(null, cancellationToken);
            if (host.Equals(greeting, StringComparison.OrdinalIgnoreCase) && workspace.Configured
                && workspace.Domains.Any(d => d.Active == true && d.Name.Equals(host[5..], StringComparison.OrdinalIgnoreCase)))
                targets = [new(host[5..], host, "", "")];
        }
        if (targets.Length == 0) return;
        if (beforeMutation is not null) await beforeMutation(cancellationToken);
        await RecoverCertificateTransactionAsync(cancellationToken);
        foreach (var target in targets)
        {
            var material = StoreCertificateLookup(target.HostName) ?? throw new AppException("mail.certificate_store_missing", 409, 409);
            await ApplyMailEnableCertificateAsync(target with { Thumbprint = material.Thumbprint, PemDirectory = "" }, material, beforeMutation, cancellationToken);
        }
    }

    private async Task<MailCertificateDto> ProbeManagedCertificateAsync(string host, string? expected, CancellationToken token)
    {
        var probes = await Task.WhenAll(new[] { 587, 465, 443 }.Select(port => EndpointProbe(host, port, port == 443 ? null : expected, token)));
        var endpoints = probes.Where(p => p.Port != 443).ToArray();
        return new() { HostName = host, Source = "mailenable-windows-store", Active = endpoints.Length == 2 && endpoints.All(p => p.Status == "ok"),
            CertificateId = expected ?? "", Endpoints = endpoints, WebCertificate = probes.Single(p => p.Port == 443),
            NotAfterUtc = endpoints.Where(p => p.NotAfterUtc.HasValue).Select(p => p.NotAfterUtc).DefaultIfEmpty().Min() };
    }

    private async Task<MailCertificateDto> ApplyMailEnableCertificateAsync(ManagedCertificate target, MailCertificateMaterial material,
        Func<CancellationToken, Task>? beforeMutation, CancellationToken token)
    {
        var validatedPem = ValidateCertificateMaterial(target.HostName, material.CertificatePem, material.PrivateKeyPem);
        using var leaf = X509Certificate2.CreateFromPem(validatedPem, material.PrivateKeyPem);
        if (!leaf.Thumbprint.Equals(material.Thumbprint, StringComparison.OrdinalIgnoreCase))
            throw new AppException("mail.certificate_invalid", 409, 409, target.HostName);
        var snapshot = JsonSerializer.Deserialize<CertificateSnapshot>(await RunComAsync(CertificateInventoryScript, token), JsonOptions)
            ?? throw new AppException("mail.settings_unmeasured", 502, 502);
        var trackedCertificates = await CertificateFiles.ReadAsync<ManagedCertificate[]>(MailEnableCertificateStatePath, [], token);
        var transaction = PlanCertificateReplacement(target.HostName, leaf, snapshot,
            trackedCertificates.Select(c => c.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var defaultName = transaction.AppliedDefaultName;
        // Keep already-served material and configuration untouched, while adopting its renewal source.
        var current = await ProbeManagedCertificateAsync(target.HostName, leaf.Thumbprint, token);
        if (transaction.AlreadyPresent && transaction.Removed.Length == 0 && snapshot.Sni == "1" && current.Active)
        {
            if (beforeMutation is not null) await beforeMutation(token);
            await SaveManagedCertificateAsync(target, token);
            current.Domain = target.Domain; current.DefaultCertificateId = snapshot.DefaultName; current.ConfigurationStatus = "ok";
            return current;
        }
        if (beforeMutation is not null) await beforeMutation(token);
        // The journal contains only public identifiers; keys remain in machine certificate stores.
        // A killed worker can recover the previous selection at the next certificate operation.
        await CertificateFiles.WriteAsync(CertificateJournalPath, transaction, token);
        var archive = leaf.Export(X509ContentType.Pfx, "");
        try
        {
            await RunVerifiedCommandAsync(BuildCertificateApplyScript(transaction, defaultName, Convert.ToBase64String(archive), validatedPem,
                CertificateFiles.ResolvePath(CertificateJournalPath)), token);
            var result = await ProbeManagedCertificateAsync(target.HostName, leaf.Thumbprint, token);
            if (!result.Active) throw new AppException("mail.certificate_not_activated", 502, 502, target.HostName);
            if (beforeMutation is not null) await beforeMutation(token);
            await SaveManagedCertificateAsync(target, token);
            await CertificateFiles.WriteAsync<CertificateTransaction?>(CertificateJournalPath, null, token);
            result.Domain = target.Domain; result.DefaultCertificateId = defaultName; result.ConfigurationStatus = "ok";
            return result;
        }
        catch
        {
            try { await RecoverCertificateTransactionAsync(CancellationToken.None); }
            catch { throw new AppException("mail.certificate_rollback_failed", 502, 502); }
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(archive); }
    }

    private async Task SaveManagedCertificateAsync(ManagedCertificate target, CancellationToken token)
    {
        var saved = await CertificateFiles.ReadAsync<ManagedCertificate[]>(MailEnableCertificateStatePath, [], token);
        await CertificateFiles.WriteAsync(MailEnableCertificateStatePath, saved.Where(s => !s.HostName.Equals(target.HostName, StringComparison.OrdinalIgnoreCase)).Append(target).ToArray(), token);
    }

    private async Task RecoverCertificateTransactionAsync(CancellationToken token)
    {
        var pending = await CertificateFiles.ReadAsync<CertificateTransaction?>(CertificateJournalPath, null, token);
        if (pending is null) return;
        if (pending.Started) await RunVerifiedCommandAsync(BuildCertificateRollbackScript(pending), token);
        await CertificateFiles.WriteAsync<CertificateTransaction?>(CertificateJournalPath, null, token);
    }

    internal static CertificateTransaction PlanCertificateReplacement(string host, X509Certificate2 replacement, CertificateSnapshot snapshot,
        IReadOnlySet<string>? managedThumbprints = null)
    {
        if (snapshot.DefaultName is null || snapshot.Sni is not ("0" or "1") || snapshot.Certificates is null || snapshot.RunningServices is null
            || !snapshot.RunningServices.Contains("MESMTPC") || snapshot.RunningServices.Any(s => s is not ("MESMTPC" or "MEIMAPS" or "MEPOPS")))
            throw new AppException("mail.settings_unmeasured", 502, 502);
        var removed = new List<string>(); var inventory = new List<string>(); var present = false;
        var replacementName = replacement.GetNameInfo(X509NameType.SimpleName, false);
        if (string.IsNullOrWhiteSpace(replacementName)) throw new AppException("mail.certificate_invalid", 409, 409, host);
        foreach (var der in snapshot.Certificates)
        {
            using var certificate = new X509Certificate2(Convert.FromBase64String(der));
            inventory.Add(certificate.Thumbprint);
            if (certificate.Thumbprint == replacement.Thumbprint) { present = true; continue; }
            var sameName = certificate.GetNameInfo(X509NameType.SimpleName, false).Equals(replacement.GetNameInfo(X509NameType.SimpleName, false), StringComparison.OrdinalIgnoreCase);
            if (!sameName && !certificate.MatchesHostname(host, true, false)) continue;
            // An unknown public certificate may belong to a third-party service whose
            // bindings Windows cannot enumerate. Only panel-managed renewals or the
            // matching initial self-signed certificate can be retired automatically.
            if (managedThumbprints?.Contains(certificate.Thumbprint) != true && !IsSelfSignedCertificate(certificate))
                throw new AppException("mail.certificate_selection_conflict", 409, 409, host);
            // MailEnable selects by name, not thumbprint. Retain every SAN served by an
            // overlapping certificate, or refuse rather than silently breaking another tenant.
            var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToArray();
            if (names.Length == 0 && !replacement.MatchesHostname(certificate.GetNameInfo(X509NameType.SimpleName, false), true, false)
                || names.Any(name => name.StartsWith("*.", StringComparison.Ordinal)
                    ? !replacement.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).Contains(name, StringComparer.OrdinalIgnoreCase)
                    : !replacement.MatchesHostname(name, true, false)))
                throw new AppException("mail.certificate_selection_conflict", 409, 409, host);
            removed.Add(certificate.Thumbprint);
        }
        var defaultName = snapshot.DefaultName.Length == 0 || snapshot.DefaultName.Equals(replacementName, StringComparison.OrdinalIgnoreCase)
            ? replacementName : snapshot.DefaultName;
        // Never leave a default name pointing at a removed overlapping certificate.
        foreach (var der in snapshot.Certificates)
        {
            using var certificate = new X509Certificate2(Convert.FromBase64String(der));
            var selectedName = snapshot.FriendlyNames?.GetValueOrDefault(certificate.Thumbprint);
            if (string.IsNullOrWhiteSpace(selectedName)) selectedName = certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (removed.Contains(certificate.Thumbprint) && selectedName.Equals(defaultName, StringComparison.OrdinalIgnoreCase)
                && !replacementName.Equals(defaultName, StringComparison.OrdinalIgnoreCase))
                throw new AppException("mail.certificate_selection_conflict", 409, 409, host);
        }
        return new(replacement.Thumbprint, snapshot.DefaultName, snapshot.Sni, removed.ToArray(), snapshot.RunningServices, present, defaultName, inventory.ToArray());
    }

    private static bool IsSelfSignedCertificate(X509Certificate2 certificate)
    {
        if (!certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)) return false;
        try
        {
            var parsed = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(certificate.RawData);
            parsed.Verify(parsed.GetPublicKey());
            return true;
        }
        catch (Org.BouncyCastle.Security.GeneralSecurityException) { return false; }
    }

    internal static readonly string CertificateInventoryScript = PlatformHeader + """

        $inventory = @(Get-ChildItem Cert:\LocalMachine\My -ErrorAction Stop)
        $certs = @($inventory | ForEach-Object { [Convert]::ToBase64String($_.RawData) })
        $friendlyNames = @{}; foreach ($certificate in $inventory) { $friendlyNames[$certificate.Thumbprint] = $certificate.FriendlyName }
        $services = @(Get-Service MESMTPC,MEIMAPS,MEPOPS -ErrorAction SilentlyContinue | Where-Object Status -eq Running | ForEach-Object Name)
        [pscustomobject]@{ defaultName = (Read-Setting 'sysDefaultCertificate'); sni = (Read-Setting 'sysSNI'); certificates = @($certs); runningServices = @($services); friendlyNames = $friendlyNames } | ConvertTo-Json -Depth 5 -Compress
        """;

    private static string TransactionHeader(CertificateTransaction transaction) => PlatformHeader + "\n$t = '" +
        PsLiteralRaw(JsonSerializer.Serialize(transaction, JsonOptions)) + "' | ConvertFrom-Json\n" + """
        $my = New-Object System.Security.Cryptography.X509Certificates.X509Store('My','LocalMachine')
        $backup = New-Object System.Security.Cryptography.X509Certificates.X509Store('AdaPanelMailEnableBackup','LocalMachine')
        $my.Open('ReadWrite'); $backup.Open('ReadWrite')
        function Restart-MailServices {
          foreach ($name in $t.runningServices) {
            $svc = Get-Service -Name $name -ErrorAction Stop
            if ($svc.Status -eq 'Running') { $svc.Stop(); $svc.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(4)) }
            $svc.Start(); $svc.WaitForStatus('Running',[TimeSpan]::FromSeconds(4))
          }
        }
        function Write-VerifiedSetting([string]$name,[string]$value) {
          Set-MailEnablePlatform -Setting $name -Value $value -ErrorAction Stop | Out-Null
          if ((Read-Setting $name) -cne $value) { throw 'Certificate setting readback failed.' }
        }
        """;

    internal static string BuildCertificateRollbackScript(CertificateTransaction transaction) => TransactionHeader(transaction) + """

        try {
          $currentDefault = Read-Setting 'sysDefaultCertificate'; $currentSni = Read-Setting 'sysSNI'
          if ($currentDefault -cne $t.defaultName -and $currentDefault -cne $t.appliedDefaultName) { throw 'Concurrent certificate selection change prevents rollback.' }
          if ($currentSni -cne $t.sni -and $currentSni -cne '1') { throw 'Concurrent SNI change prevents rollback.' }
          foreach ($thumb in $t.removed) {
            if (@($my.Certificates | Where-Object Thumbprint -eq $thumb).Count -eq 0) {
              $old = @($backup.Certificates | Where-Object Thumbprint -eq $thumb)
              if ($old.Count -ne 1) { throw 'Certificate rollback archive missing.' }
              $my.Add($old[0])
            }
          }
          Write-VerifiedSetting 'sysDefaultCertificate' $t.defaultName
          Write-VerifiedSetting 'sysSNI' $t.sni
          if (-not $t.alreadyPresent) {
            foreach ($cert in @($my.Certificates | Where-Object Thumbprint -eq $t.thumbprint)) { $my.Remove($cert) }
          }
          Restart-MailServices
          'OK'
        } finally { $my.Close(); $backup.Close() }
        """;

    internal static string BuildCertificateApplyScript(CertificateTransaction transaction, string defaultName, string archiveBase64, string chainPem, string journalPath) =>
        TransactionHeader(transaction) + $$"""

        try {
          if ((Read-Setting 'sysDefaultCertificate') -cne $t.defaultName -or (Read-Setting 'sysSNI') -cne $t.sni) { throw 'Certificate configuration changed concurrently.' }
          $inventory = @($my.Certificates | ForEach-Object Thumbprint | Sort-Object)
          if (($inventory -join ',') -cne (@($t.inventoryThumbprints | Sort-Object) -join ',')) { throw 'Certificate inventory changed concurrently.' }
          # Refuse to remove a certificate referenced by HTTP.sys/IIS. Its operator must
          # migrate that binding first; moving it would otherwise break another service.
          $bindings = (& netsh.exe http show sslcert 2>&1 | Out-String)
          if ($LASTEXITCODE -ne 0) { throw 'HTTP certificate bindings could not be measured.' }
          $sharedThumbprints = @()
          $rdpRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations'
          if (Test-Path $rdpRoot) {
            foreach ($listener in Get-ChildItem $rdpRoot -ErrorAction Stop) {
              $rdp = Get-ItemProperty $listener.PSPath -ErrorAction Stop
              if ($null -ne $rdp.SSLCertificateSHA1Hash) {
                if ($rdp.SSLCertificateSHA1Hash -is [byte[]]) { $sharedThumbprints += ([BitConverter]::ToString($rdp.SSLCertificateSHA1Hash) -replace '-','') }
                else { $sharedThumbprints += [string]$rdp.SSLCertificateSHA1Hash }
              }
            }
          }
          $winrmRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WSMAN\Listener'
          if (Test-Path $winrmRoot) {
            foreach ($listener in Get-ChildItem $winrmRoot -ErrorAction Stop) {
              $winrm = Get-ItemProperty $listener.PSPath -ErrorAction Stop
              if ($null -ne $winrm.CertificateThumbprint) { $sharedThumbprints += [string]$winrm.CertificateThumbprint }
            }
          }
          $winrmService = Get-Service WinRM -ErrorAction SilentlyContinue
          if ($null -ne $winrmService -and $winrmService.Status -eq 'Running') {
            foreach ($listener in Get-WSManInstance -ResourceURI winrm/config/listener -Enumerate -ErrorAction Stop) {
              if ($listener.Transport -eq 'HTTPS') { $sharedThumbprints += [string]$listener.CertificateThumbprint }
            }
          }
          foreach ($thumb in $t.removed) {
            if ($bindings -match [regex]::Escape($thumb) -or $sharedThumbprints -contains $thumb) { throw 'Certificate is shared with another service.' }
            $old = @($my.Certificates | Where-Object Thumbprint -eq $thumb)
            if ($old.Count -ne 1) { throw 'Certificate inventory changed concurrently.' }
          }
          # Commit a durable mutation marker only after every non-mutating precondition.
          # A preflight rejection must not roll back changes made by another administrator.
          $t.started = $true
          $journal = '{{PsLiteralRaw(journalPath)}}'; $journalTemp = $journal + '.' + [Guid]::NewGuid().ToString('N')
          [IO.File]::WriteAllText($journalTemp,($t | ConvertTo-Json -Depth 8 -Compress),[Text.Encoding]::UTF8)
          [IO.File]::Replace($journalTemp,$journal,$null)
          foreach ($thumb in $t.removed) {
            $old = @($my.Certificates | Where-Object Thumbprint -eq $thumb)
            $backup.Add($old[0])
          }
          $cert = @($my.Certificates | Where-Object { $_.Thumbprint -eq $t.thumbprint -and $_.HasPrivateKey }) | Select-Object -First 1
          if ($null -eq $cert) {
            $bytes = [Convert]::FromBase64String('{{archiveBase64}}')
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
            try { $cert.Import($bytes,'',[System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable); $my.Add($cert) }
            finally { [Array]::Clear($bytes,0,$bytes.Length) }
          }
          if ($cert.Thumbprint -ne $t.thumbprint -or -not $cert.HasPrivateKey) { throw 'Imported certificate does not match.' }
          # Import only supplied intermediates into CA; never add a new trusted root.
          $pem = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Convert.ToBase64String(Encoding.UTF8.GetBytes(chainPem))}}'))
          $ca = New-Object System.Security.Cryptography.X509Certificates.X509Store('CA','LocalMachine'); $ca.Open('ReadWrite')
          try {
            foreach ($match in [regex]::Matches($pem,'(?s)-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----')) {
              $part = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(,[Convert]::FromBase64String(($match.Groups[1].Value -replace '\s','')))
              if ($part.Thumbprint -ne $cert.Thumbprint) { $ca.Add($part) }
              $part.Dispose()
            }
          } finally { $ca.Close() }
          $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
          if ($null -eq $key) { $key = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($cert) }
          if ($key -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            $keyDirectory = Join-Path $env:ProgramData 'Microsoft\Crypto\RSA\MachineKeys'; $keyName = $key.CspKeyContainerInfo.UniqueKeyContainerName
          } elseif ($null -ne $key -and $null -ne $key.PSObject.Properties['Key']) {
            $keyDirectory = Join-Path $env:ProgramData 'Microsoft\Crypto\Keys'; $keyName = $key.Key.UniqueName
          } else { throw 'Certificate private key provider is unsupported.' }
          if ([IO.Path]::GetFileName($keyName) -cne $keyName) { throw 'Invalid private key container.' }
          $keyPath = Join-Path $keyDirectory $keyName; $acl = Get-Acl -LiteralPath $keyPath -ErrorAction Stop
          foreach ($name in $t.runningServices) {
            $service = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction Stop
            if ($null -eq $service -or [string]::IsNullOrWhiteSpace($service.StartName)) { throw 'Service identity could not be measured.' }
            $identity = $service.StartName
            if ($identity -eq 'LocalSystem') { $identity = 'NT AUTHORITY\SYSTEM' }
            elseif ($identity.StartsWith('.\')) { $identity = $env:COMPUTERNAME + $identity.Substring(1) }
            $account = New-Object System.Security.Principal.NTAccount($identity)
            $sid = $account.Translate([System.Security.Principal.SecurityIdentifier])
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid,'Read','Allow')))
          }
          Set-Acl -LiteralPath $keyPath -AclObject $acl -ErrorAction Stop
          $key.Dispose()
          foreach ($thumb in $t.removed) { foreach ($old in @($my.Certificates | Where-Object Thumbprint -eq $thumb)) { $my.Remove($old) } }
          $providerNames = @([MailEnable.Administration.Certificates]::GetSSLCertificateList() | ForEach-Object sKey)
          if ($providerNames -notcontains '{{PsLiteralRaw(defaultName)}}') { throw 'MailEnable cannot resolve the selected certificate name.' }
          Write-VerifiedSetting 'sysDefaultCertificate' '{{PsLiteralRaw(defaultName)}}'
          Write-VerifiedSetting 'sysSNI' '1'
          Restart-MailServices
          'OK'
        } finally { $my.Close(); $backup.Close() }
        """;
}
