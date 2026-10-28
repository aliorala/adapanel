using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;

namespace AdaPanel.Infrastructure.Ftp;

public sealed partial class FtpService
{
    internal sealed record FtpCertificateCandidate(string Thumbprint, string StoreName, string Subject, DateTime NotAfterUtc);
    internal sealed class FtpCertificateBinding
    {
        public string Thumbprint { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
        public int ControlPolicy { get; set; }
        public int DataPolicy { get; set; }
    }
    internal Func<string, CancellationToken, Task<FtpCertificateCandidate>> CertificateSelector { get; set; } = FindCertificateAsync;
    internal Func<CancellationToken, Task<bool>>? CertificateServiceReady { get; set; }
    internal Func<string, string, CancellationToken, Task<FtpCertificateDto>> CertificateProbe { get; set; } = FtpTlsProbe.ProbeAsync;

    internal static string NormalizeCertificateHost(string host)
    {
        host = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || host.Length > 253 || !host.Contains('.') || host.Contains('*'))
            throw new AppException("ftp.certificate_invalid_host", 400, 400, "Enter the DNS name used by FTPS clients, without protocol or port.");
        return host;
    }

    private static Task<FtpCertificateCandidate> FindCertificateAsync(string host, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var candidates = new List<FtpCertificateCandidate>();
        foreach (var storeName in new[] { "My", "WebHosting" })
        {
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
            catch (System.Security.Cryptography.CryptographicException) { continue; }
            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsUsableCertificate(certificate, host)) continue;
                    candidates.Add(new(certificate.Thumbprint, storeName, certificate.Subject, certificate.NotAfter.ToUniversalTime()));
                }
            }
        }
        return Task.FromResult(candidates.OrderByDescending(item => item.NotAfterUtc).FirstOrDefault()
            ?? throw new AppException("ftp.certificate_not_found", 409, 409,
                "No trusted, unexpired server certificate with a private key and matching SAN exists in LocalMachine My/WebHosting. Obtain a certificate for the FTPS name first."));
    }

    internal static bool IsUsableCertificate(X509Certificate2 certificate, string host)
    {
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow
            || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || !certificate.MatchesHostname(host, true, false)) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(certificate);
    }

    private async Task<string> GetFtpClientHostAsync(string fallback, CancellationToken token)
    {
        var state = await fileStore.ReadAsync(TlsStatePath, new FtpTlsState(), token);
        return string.IsNullOrWhiteSpace(state.HostName) ? fallback : NormalizeCertificateHost(state.HostName);
    }

    public async Task<FtpCertificateDto> GetCertificateAsync(CancellationToken cancellationToken = default)
    {
        var state = await fileStore.ReadAsync(TlsStatePath, new FtpTlsState(), cancellationToken);
        if (string.IsNullOrWhiteSpace(state.HostName)) return new FtpCertificateDto
        { CertificateDetail = "No FTPS DNS name is tracked. A saved TLS policy alone does not verify a certificate or AUTH TLS." };
        var binding = await ReadCertificateBindingAsync(cancellationToken);
        if (!string.Equals(state.CertificateThumbprint, binding.Thumbprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.CertificateStoreName, binding.StoreName, StringComparison.OrdinalIgnoreCase))
            return new FtpCertificateDto { HostName = state.HostName, CertificateThumbprint = binding.Thumbprint,
                CertificateStoreName = binding.StoreName, CertificateVerified = false, CertificateDetail = "The FTP binding differs from the tracked certificate." };
        var measured = await CertificateProbe(state.HostName, binding.Thumbprint, cancellationToken);
        measured.CertificateStoreName = binding.StoreName;
        return measured;
    }

    public Task<FtpCertificateDto> ApplyCertificateAsync(ApplyFtpCertificateRequest request,
        Func<CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforeMutation);
        var host = NormalizeCertificateHost(request.HostName);
        return InfrastructureLock.RunExclusiveAsync(InfrastructureLockKey,
            () => ApplyCertificateCoreAsync(host, beforeMutation, cancellationToken), cancellationToken);
    }

    public Task<FtpCertificateDto?> SyncCertificateAsync(Func<CancellationToken, Task>? beforeMutation,
        CancellationToken cancellationToken = default) => InfrastructureLock.RunExclusiveAsync<FtpCertificateDto?>(InfrastructureLockKey, async () =>
    {
        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
        var state = await fileStore.ReadAsync(TlsStatePath, new FtpTlsState(), cancellationToken);
        if (string.IsNullOrWhiteSpace(state.HostName)) return null;
        return await ApplyCertificateCoreAsync(NormalizeCertificateHost(state.HostName), beforeMutation, cancellationToken);
    }, cancellationToken);

    private async Task<FtpCertificateDto> ApplyCertificateCoreAsync(string host, Func<CancellationToken, Task>? beforeMutation, CancellationToken token)
    {
        await ApproveInfrastructureAsync(beforeMutation, token);
        var ready = CertificateServiceReady is { } readyProbe ? await readyProbe(token) : (await GetServerStatusAsync(token)).IsConfigured;
        await ApproveInfrastructureAsync(beforeMutation, token);
        if (!ready) throw new AppException("ftp.certificate_service_unverified", 409, 409,
            "The running port 21 listener could not be verified as the panel IIS FTP service. No certificate was changed.");
        var selected = await CertificateSelector(host, token);
        await ApproveInfrastructureAsync(beforeMutation, token);
        var previous = await ReadCertificateBindingAsync(token);
        await ApproveInfrastructureAsync(beforeMutation, token);
        if (previous.ControlPolicy is not (0 or 1) || previous.DataPolicy is not (0 or 1))
            throw new AppException("ftp.certificate_policy_blocked", 409, 409, "IIS currently forbids TLS on a control/data channel. Existing policies were preserved; enable an appropriate FTPS policy explicitly.");
        var changed = !string.Equals(previous.Thumbprint, selected.Thumbprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.StoreName, selected.StoreName, StringComparison.OrdinalIgnoreCase);
        var mutationStarted = false;
        try
        {
            if (changed)
            {
                await ApproveInfrastructureAsync(beforeMutation, token);
                mutationStarted = true;
                await WriteCertificateBindingAsync(selected.Thumbprint, selected.StoreName, previous, beforeMutation, token);
                await ApproveInfrastructureAsync(beforeMutation, token);
            }
            var binding = await ReadCertificateBindingAsync(token);
            if (!string.Equals(binding.Thumbprint, selected.Thumbprint, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(binding.StoreName, selected.StoreName, StringComparison.OrdinalIgnoreCase)
                || binding.ControlPolicy != previous.ControlPolicy || binding.DataPolicy != previous.DataPolicy)
                throw new AppException("ftp.certificate_binding_failed", 500, 500, "Certificate binding/policy read-back did not match; no success was recorded.");
            var measured = await CertificateProbe(host, selected.Thumbprint, token);
            await ApproveInfrastructureAsync(beforeMutation, token);
            if (measured.CertificateVerified != true)
                throw new AppException("ftp.certificate_probe_failed", 502, 502, measured.CertificateDetail ?? "Strict AUTH TLS verification failed.");
            measured.CertificateStoreName = selected.StoreName;
            measured.CertificateSubject ??= selected.Subject;
            measured.CertificateNotAfterUtc ??= selected.NotAfterUtc;
            await fileStore.UpdateAsync(TlsStatePath, new FtpTlsState(), current =>
            {
                current.HostName = host;
                current.CertificateThumbprint = selected.Thumbprint;
                current.CertificateStoreName = selected.StoreName;
                return current;
            }, ct => ApproveInfrastructureAsync(beforeMutation, ct), token);
            return measured;
        }
        catch (Exception original)
        {
            if (!mutationStarted) throw;
            try
            {
                // Rollback is a native mutation too: expired/revoked authority cannot perform it.
                await ApproveInfrastructureAsync(beforeMutation, token);
                var actual = await ReadCertificateBindingAsync(token);
                if ((!string.Equals(actual.Thumbprint, previous.Thumbprint, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(actual.Thumbprint, selected.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    || (!string.Equals(actual.StoreName, previous.StoreName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(actual.StoreName, selected.StoreName, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("FTP binding was changed by another administrator; rollback did not overwrite it.");
                await WriteCertificateBindingAsync(previous.Thumbprint, previous.StoreName, actual, beforeMutation, token);
            }
            catch (Exception rollback)
            { throw new AppException("ftp.certificate_rollback_failed", 500, 500,
                $"Certificate activation failed ({original.Message}); rollback could not complete ({rollback.Message}). Inspect the actual FTP binding before retrying."); }
            throw;
        }
    }

    private async Task<FtpCertificateBinding> ReadCertificateBindingAsync(CancellationToken token)
    {
        var result = await RunStrictScriptAsync($$"""
            $ErrorActionPreference = 'Stop'
            Import-Module WebAdministration -ErrorAction Stop
            $site = Get-Item 'IIS:\Sites\{{FtpSiteName}}' -ErrorAction Stop
            $ssl = $site.ftpServer.security.ssl
            if ($null -eq $ssl -or $null -eq $ssl.controlChannelPolicy -or $null -eq $ssl.dataChannelPolicy) { throw 'FTP SSL configuration could not be measured.' }
            $hash = $ssl.serverCertHash
            if ($hash -is [byte[]]) { $hash = [BitConverter]::ToString($hash).Replace('-', '') }
            [pscustomobject]@{ thumbprint = [string]$hash; storeName = [string]$ssl.serverCertStoreName; controlPolicy = [int]$ssl.controlChannelPolicy; dataPolicy = [int]$ssl.dataChannelPolicy } | ConvertTo-Json -Compress
            """, token);
        if (!result.Success) throw new AppException("ftp.certificate_binding_unreadable", 500, 500, BuildCommandDetails(result));
        return JsonSerializer.Deserialize<FtpCertificateBinding>(result.StandardOutput, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new AppException("ftp.certificate_binding_unreadable", 500, 500);
    }

    private async Task WriteCertificateBindingAsync(string thumbprint, string storeName, FtpCertificateBinding expected, Func<CancellationToken, Task>? beforeMutation, CancellationToken token)
    {
        // Compare-and-set avoids overwriting an administrator's concurrent native IIS edit.
        var result = await InfrastructureLock.RunExclusiveAsync(AdaPanel.Core.Interfaces.ConcurrencyKeys.Iis, async () =>
        {
            await ApproveInfrastructureAsync(beforeMutation, token);
            return await RunStrictScriptAsync($$"""
            $ErrorActionPreference = 'Stop'
            Import-Module WebAdministration -ErrorAction Stop
            $path = 'IIS:\Sites\{{FtpSiteName}}'
            $ssl = (Get-Item $path -ErrorAction Stop).ftpServer.security.ssl
            if ($null -eq $ssl -or $null -eq $ssl.controlChannelPolicy -or $null -eq $ssl.dataChannelPolicy) { throw 'FTP SSL configuration could not be measured.' }
            $hash = $ssl.serverCertHash
            if ($hash -is [byte[]]) { $hash = [BitConverter]::ToString($hash).Replace('-', '') }
            if ([string]$hash -ine '{{EscapePowerShellLiteral(expected.Thumbprint)}}' -or [string]$ssl.serverCertStoreName -ine '{{EscapePowerShellLiteral(expected.StoreName)}}' -or [int]$ssl.controlChannelPolicy -ne {{expected.ControlPolicy}} -or [int]$ssl.dataChannelPolicy -ne {{expected.DataPolicy}}) { throw 'FTP certificate/policies changed concurrently; no overwrite performed.' }
            if ('{{EscapePowerShellLiteral(thumbprint)}}') {
              $certificate = Get-Item -LiteralPath 'Cert:\LocalMachine\{{EscapePowerShellLiteral(storeName)}}\{{EscapePowerShellLiteral(thumbprint)}}' -ErrorAction Stop
              if (-not $certificate.HasPrivateKey) { throw 'FTP certificate private key unavailable.' }
            }
            Set-ItemProperty $path -Name ftpServer.security.ssl.serverCertStoreName -Value '{{EscapePowerShellLiteral(storeName)}}' -ErrorAction Stop
            Set-ItemProperty $path -Name ftpServer.security.ssl.serverCertHash -Value '{{EscapePowerShellLiteral(thumbprint)}}' -ErrorAction Stop
            Restart-Service FTPSVC -Force -ErrorAction Stop
            (Get-Service FTPSVC -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
            """, token);
        }, token);
        if (!result.Success) throw new AppException("ftp.certificate_binding_failed", 500, 500, BuildCommandDetails(result));
    }
}
