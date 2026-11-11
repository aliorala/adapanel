using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Enums;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Ftp;

public sealed partial class FtpService
{
    private static string[] TrackedHosts(FtpTlsState state) => state.HostNames
        .Concat(string.IsNullOrWhiteSpace(state.HostName) ? [] : new[] { state.HostName })
        .Select(NormalizeCertificateHost).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private async Task<FtpCertificateDto> ProbeCertificateHostsAsync(string primary, string[] hosts, string thumbprint, CancellationToken token)
    {
        FtpCertificateDto? result = null;
        foreach (var host in hosts)
        {
            var measured = await CertificateProbe(host, thumbprint, token);
            result ??= measured;
            if (measured.CertificateVerified != true) { result = measured; break; }
        }
        result ??= new FtpCertificateDto { CertificateVerified = false };
        result.HostName = primary;
        result.HostNames = hosts;
        return result;
    }

    private async Task EnsureSiteCertificateAsync(string siteName, Func<CancellationToken, Task> approve, CancellationToken token)
    {
        var state = await fileStore.ReadAsync(TlsStatePath, new FtpTlsState(), token);
        if (string.IsNullOrWhiteSpace(state.HostName)) return;
        var site = await iisSiteService.GetSiteAsync(siteName, token);
        var host = AccountCertificateHost(site);
        if (string.IsNullOrWhiteSpace(host) || TrackedHosts(state).Contains(host, StringComparer.OrdinalIgnoreCase)) return;
        await InfrastructureLock.RunExclusiveAsync(InfrastructureLockKey,
            () => ApplyCertificateCoreAsync(state.HostName, [NormalizeCertificateHost(host)], approve, token), token);
    }

    // Existing accounts enter the same normal renewal path; no unrelated IIS sites are enrolled.
    private async Task<string[]> AccountCertificateHostsAsync(CancellationToken token)
    {
        var accounts = await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), token);
        var hosts = new List<string>();
        foreach (var siteName in accounts.Where(a => a.IsEnabled).Select(a => a.SiteName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var site = await iisSiteService.GetSiteAsync(siteName, token);
            var host = AccountCertificateHost(site);
            if (host is not null) hosts.Add(NormalizeCertificateHost(host));
        }
        return hosts.ToArray();
    }

    internal static string? AccountCertificateHost(SiteDto site)
    {
        var hosts = site.Bindings.Where(binding => binding.Protocol is "http" or "https").Select(binding => binding.HostName)
            .Where(host => Uri.CheckHostName(host) == UriHostNameType.Dns && host.Contains('.') && !host.Contains('*'))
            .ToArray();
        return hosts.FirstOrDefault(host => host.Equals(site.Name, StringComparison.OrdinalIgnoreCase))
            ?? hosts.FirstOrDefault(host => !host.StartsWith("mail.", StringComparison.OrdinalIgnoreCase)
                && !host.StartsWith("webmail.", StringComparison.OrdinalIgnoreCase))
            ?? hosts.FirstOrDefault();
    }

    internal static string BuildSanArguments(string[] hosts, string email)
    {
        if (hosts.Length is 0 or > 100 || hosts.Any(h => !AcmeArgumentRules.IsCommandSafeHost(h) || h.Contains('*'))
            || !AcmeArgumentRules.IsCommandSafeEmail(email))
            throw new AppException("ftp.certificate_acme_arguments_invalid", 400, 400);
        // Self-hosting HTTP-01 uses HTTP.sys alongside IIS; no HTTPS/mail installation plugin.
        // Keep previous certificates: rollback and existing web/mail bindings may still use them.
        return $"--source manual --host \"{string.Join(',', hosts)}\" --id AdaPanel-FTPS --friendlyname AdaPanel-FTPS --validation selfhosting --store certificatestore --certificatestore My --keepexisting --installation none --emailaddress \"{email.Trim()}\" --accepttos --baseuri https://acme-v02.api.letsencrypt.org/";
    }

    private async Task IssueSanCertificateAsync(string[] hosts, Func<CancellationToken, Task>? approve, CancellationToken token)
    {
        var contact = await fileStore.ReadAsync(AcmeContactSettings.Path, new AcmeContactSettings(), token);
        var args = BuildSanArguments(hosts, contact.Email);
        var component = ftpCertificateDb is null ? null : await ftpCertificateDb.InstalledComponents.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ComponentId == "win-acme" && c.Status == ComponentInstallStatus.Installed, token);
        var executable = component is null || !Directory.Exists(component.InstallDir) ? null
            : Directory.EnumerateFiles(component.InstallDir, "wacs.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (executable is null) throw new AppException("ftp.certificate_acme_missing", 409, 409, "Install win-acme and configure the ACME contact email before requesting the shared FTPS certificate.");
        await ApproveInfrastructureAsync(approve, token);
        var result = await ProcessRunner.RunAsync(executable, args, timeout: TimeSpan.FromMinutes(10), cancellationToken: token);
        await ApproveInfrastructureAsync(approve, token);
        if (!result.Success) throw new AppException("ftp.certificate_acme_failed", 502, 502,
            "Shared FTPS certificate issuance failed. Existing FTP binding was preserved. Check ACME HTTP-01/DNS reachability and win-acme logs.");
        // Exit code is not certificate evidence: the caller reselects a trusted SAN certificate.
    }
}
